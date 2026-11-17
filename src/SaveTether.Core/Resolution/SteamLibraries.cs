using System.Globalization;
using SaveTether.Core.FileSystem;

namespace SaveTether.Core.Resolution;

/// <summary>
/// Finds Steam's libraries and the Proton prefixes inside them, on Linux, by reading Steam's own
/// files: libraryfolders.vdf for which library holds a game, shortcuts.vdf for the IDs Steam gave
/// non-Steam games. Reads only.
/// </summary>
internal sealed class SteamLibraries
{
    private readonly string _home;
    private readonly string _dataHome;

    /// <param name="home">The user's home folder.</param>
    /// <param name="dataHome">The XDG data folder, normally ~/.local/share.</param>
    public SteamLibraries(string home, string dataHome)
    {
        _home = home;
        _dataHome = dataHome;
    }

    /// <summary>One Steam library folder and the app IDs it says it holds (empty when it does not say).</summary>
    public sealed record Library(string Path, IReadOnlySet<ulong> Apps);

    /// <summary>
    /// Steam installs on this device, each the folder holding steamapps: the native one (reached by
    /// several links) and the Flatpak one. Links are followed so one install is listed once.
    /// </summary>
    public IReadOnlyList<string> FindRoots()
    {
        string[] candidates =
        [
            Path.Combine(_home, ".steam", "steam"),
            Path.Combine(_home, ".steam", "root"),
            Path.Combine(_dataHome, "Steam"),
            Path.Combine(_home, ".local", "share", "Steam"),
            Path.Combine(_home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"),
            Path.Combine(_home, ".var", "app", "com.valvesoftware.Steam", "data", "Steam"),
        ];

        return Distinct(candidates.Where(root => Directory.Exists(Path.Combine(root, "steamapps"))));
    }

    /// <summary>
    /// Every library of every Steam install, with what libraryfolders.vdf says each holds. A
    /// file that cannot be read is reported in <paramref name="problems"/> and its install's own
    /// folder is still used as a library.
    /// </summary>
    public IReadOnlyList<Library> FindLibraries(List<string> problems)
    {
        var byPath = new Dictionary<string, (string Path, HashSet<ulong> Apps)>(StringComparer.Ordinal);
        void Add(string path, IEnumerable<ulong> apps)
        {
            var key = Canonical(path);
            if (!byPath.TryGetValue(key, out var entry))
            {
                entry = (path, []);
                byPath[key] = entry;
            }

            entry.Apps.UnionWith(apps);
        }

        foreach (var root in FindRoots())
        {
            Add(root, []);
            var file = new[] { Path.Combine(root, "steamapps", "libraryfolders.vdf"), Path.Combine(root, "config", "libraryfolders.vdf") }
                .FirstOrDefault(File.Exists);
            if (file is null)
            {
                continue;
            }

            try
            {
                var top = Vdf.ParseText(File.ReadAllText(file)).Get("libraryfolders")
                    ?? throw new VdfException("it has no \"libraryfolders\" section");
                foreach (var (key, node) in top.Children ?? [])
                {
                    if (node.Value is { } oldStylePath && ulong.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                    {
                        // Before 2021 each library was a numbered line holding only its path.
                        Add(oldStylePath, []);
                    }
                    else if (node.Get("path")?.Value is { Length: > 0 } path)
                    {
                        Add(path, ParseAppIds(node.Get("apps")));
                    }
                }
            }
            catch (Exception e) when (e is VdfException or IOException or UnauthorizedAccessException)
            {
                problems.Add($"{file} could not be read: {e.Message}");
            }
        }

        return [.. byPath.Values.Select(entry => new Library(entry.Path, entry.Apps))];
    }

    /// <summary>
    /// The compatdata folder of a Steam game: in the library libraryfolders.vdf says holds it, else
    /// in the one library that has a compatdata folder for it.
    /// </summary>
    public ResolveResult ResolveCompatData(ulong appId)
    {
        var problems = new List<string>();
        var libraries = FindLibraries(problems);
        if (libraries.Count == 0)
        {
            return ResolveResult.Fail(ResolveProblem.Missing, "Steam was not found on this device." + Explain(problems));
        }

        var id = appId.ToString(CultureInfo.InvariantCulture);
        var holding = libraries.Where(library => library.Apps.Contains(appId)).ToList();
        if (holding.Count == 1)
        {
            return ResolveResult.Ok(CompatData(holding[0].Path, id));
        }

        if (holding.Count > 1)
        {
            return ResolveResult.Fail(
                ResolveProblem.Ambiguous,
                $"Steam lists app {id} in more than one library ({string.Join(", ", holding.Select(l => l.Path))}).");
        }

        return FindExistingCompatData(libraries, id, $"Steam app {id} is not installed in any Steam library on this device." + Explain(problems));
    }

    /// <summary>The app manifest of an installed Steam game, in whichever library has one; null when none does.</summary>
    public string? FindAppManifest(ulong appId)
    {
        var name = $"appmanifest_{appId.ToString(CultureInfo.InvariantCulture)}.acf";
        return FindLibraries([])
            .Select(library => Path.Combine(library.Path, "steamapps", name))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// The compatdata folder of a non-Steam game added to Steam, found by its name in
    /// shortcuts.vdf. Steam makes the folder the first time the game runs with Proton.
    /// </summary>
    public ResolveResult ResolveShortcutCompatData(string name)
    {
        var problems = new List<string>();
        var ids = new SortedSet<ulong>();
        var sawShortcutWithoutId = false;
        foreach (var root in FindRoots())
        {
            var userdata = Path.Combine(root, "userdata");
            if (!Directory.Exists(userdata))
            {
                continue;
            }

            foreach (var user in Directory.EnumerateDirectories(userdata))
            {
                var file = Path.Combine(user, "config", "shortcuts.vdf");
                if (!File.Exists(file))
                {
                    continue;
                }

                try
                {
                    var shortcuts = Vdf.ParseBinary(File.ReadAllBytes(file)).Get("shortcuts");
                    foreach (var (_, shortcut) in shortcuts?.Children ?? [])
                    {
                        if (!string.Equals(shortcut.Get("AppName")?.Value, name, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        if (ulong.TryParse(shortcut.Get("appid")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                        {
                            ids.Add(id);
                        }
                        else
                        {
                            sawShortcutWithoutId = true;
                        }
                    }
                }
                catch (Exception e) when (e is VdfException or IOException or UnauthorizedAccessException)
                {
                    problems.Add($"{file} could not be read: {e.Message}");
                }
            }
        }

        if (ids.Count == 0)
        {
            var reason = sawShortcutWithoutId
                ? $"The Steam shortcut \"{name}\" has no app ID yet. Start Steam once so it assigns one."
                : $"No Steam shortcut is named \"{name}\".";
            return ResolveResult.Fail(ResolveProblem.Missing, reason + Explain(problems));
        }

        if (ids.Count > 1)
        {
            return ResolveResult.Fail(
                ResolveProblem.Ambiguous,
                $"More than one Steam shortcut is named \"{name}\" (app IDs {string.Join(", ", ids)}).");
        }

        var libraries = FindLibraries(problems);
        var appId = ids.Min.ToString(CultureInfo.InvariantCulture);
        return FindExistingCompatData(
            libraries,
            appId,
            $"The Steam shortcut \"{name}\" (app ID {appId}) has no Proton prefix yet. Run it once in Steam with Proton." + Explain(problems));
    }

    private static ResolveResult FindExistingCompatData(IReadOnlyList<Library> libraries, string id, string missing)
    {
        var existing = libraries.Select(library => CompatData(library.Path, id)).Where(Directory.Exists).ToList();
        return existing.Count switch
        {
            1 => ResolveResult.Ok(existing[0]),
            0 => ResolveResult.Fail(ResolveProblem.Missing, missing),
            _ => ResolveResult.Fail(
                ResolveProblem.Ambiguous,
                $"More than one Steam library has a compatdata folder for app {id} ({string.Join(", ", existing)})."),
        };
    }

    private static string CompatData(string library, string id) => Path.Combine(library, "steamapps", "compatdata", id);

    private static IEnumerable<ulong> ParseAppIds(VdfNode? apps) =>
        (apps?.Children ?? [])
            .Select(pair => ulong.TryParse(pair.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : (ulong?)null)
            .OfType<ulong>();

    private static string Explain(List<string> problems) => problems.Count == 0 ? string.Empty : " " + string.Join(" ", problems);

    private static List<string> Distinct(IEnumerable<string> paths)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return [.. paths.Where(path => seen.Add(Canonical(path)))];
    }

    /// <summary>The path with links followed, so two routes to one folder compare equal.</summary>
    private static string Canonical(string path)
    {
        try
        {
            return PathRules.Normalize(PathRules.ResolveAll(path));
        }
        catch (Exception e) when (e is SafeFsException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return PathRules.Normalize(path);
        }
    }
}
