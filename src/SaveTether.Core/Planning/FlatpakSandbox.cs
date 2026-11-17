using System.Diagnostics;
using SaveTether.Core.FileSystem;

namespace SaveTether.Core.Planning;

/// <summary>The flatpak command, as far as SaveTether uses it.</summary>
public interface IFlatpak
{
    /// <summary>"flatpak info --show-permissions &lt;id&gt;", or null when it could not be read (no flatpak command, app not installed).</summary>
    string? ShowPermissions(string appId);

    /// <summary>"flatpak override --user --show &lt;id&gt;": this user's overrides only. Null when they could not be read.</summary>
    string? ShowUserOverrides(string appId);

    /// <summary>"flatpak override --user &lt;options&gt; &lt;id&gt;". True when it succeeded.</summary>
    bool OverrideForUser(string appId, IReadOnlyList<string> options);
}

/// <summary>Runs the flatpak command.</summary>
public sealed class SystemFlatpak : IFlatpak
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public string? ShowPermissions(string appId) => Run(["info", "--show-permissions", appId]);

    public string? ShowUserOverrides(string appId) => Run(["override", "--user", "--show", appId]);

    public bool OverrideForUser(string appId, IReadOnlyList<string> options) =>
        Run(["override", "--user", .. options, appId]) is not null;

    /// <summary>Standard output when the command exits 0; null when it cannot be run, fails or hangs.</summary>
    private static string? Run(IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo("flatpak")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(Timeout))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            return process.ExitCode == 0 ? output.GetAwaiter().GetResult() : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}

/// <summary>
/// What a Flatpak app's sandbox lets it see of the shared folder. A link inside the app's own
/// folder (~/.var/app/&lt;id&gt;/) is followed inside the sandbox, so it only works when the sandbox
/// can write where it points (design 2.1, "Flatpak sandboxes").
/// </summary>
/// <param name="AppId">The Flatpak app whose folder holds the save folder.</param>
/// <param name="CanReach">True or false when the permissions were read; null when they could not be.</param>
/// <param name="Unreachable">The paths the sandbox cannot write, when <paramref name="CanReach"/> is false.</param>
/// <param name="Grants">The folders to grant: the savetether folder, in each form that is missing.</param>
/// <param name="FixCommand">The command that grants them, when <paramref name="CanReach"/> is false.</param>
public sealed record SandboxReach(
    string AppId, bool? CanReach, IReadOnlyList<string> Unreachable, IReadOnlyList<string> Grants, string? FixCommand);

/// <summary>A grant of the savetether folder found in a Flatpak app's user overrides.</summary>
/// <param name="RemoveCommand">The command that removes it.</param>
public sealed record SandboxGrant(string AppId, IReadOnlyList<string> Paths, string RemoveCommand);

/// <summary>
/// The Flatpak precheck (plan P2.6): for a save folder inside a Flatpak app's folder, reads the
/// app's filesystem permissions and works out whether it can write the shared folder. It can also
/// grant that one folder, and take the grant back, through "flatpak override --user".
/// </summary>
public sealed class FlatpakCheck
{
    private readonly IFlatpak _flatpak;
    private readonly string _home;
    private readonly Func<string, string?> _getVariable;

    public FlatpakCheck(IFlatpak flatpak, string home, Func<string, string?> getVariable)
    {
        ArgumentNullException.ThrowIfNull(flatpak);
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(getVariable);
        _flatpak = flatpak;
        _home = home;
        _getVariable = getVariable;
    }

    /// <summary>The check for this machine: on Linux it runs the flatpak command; elsewhere there are no Flatpaks.</summary>
    public static FlatpakCheck? ForSystem() => OperatingSystem.IsLinux()
        ? new FlatpakCheck(new SystemFlatpak(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetEnvironmentVariable)
        : null;

    /// <summary>The Flatpak app whose folder (~/.var/app/&lt;id&gt;) holds <paramref name="source"/>, or null.</summary>
    public string? AppOwning(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var apps = Path.Combine(_home, ".var", "app");
        if (!PathRules.IsSameOrInside(source, apps) || PathRules.AreSame(source, apps))
        {
            return null;
        }

        var relative = Path.GetRelativePath(PathRules.Normalize(apps), PathRules.Normalize(source));
        var id = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return id.Length > 0 && id != ".." ? id : null;
    }

    /// <summary>
    /// Null when the save folder is not inside a Flatpak app's folder. Otherwise whether the sandbox
    /// can write the shared folder, both as the link names it (<paramref name="target"/>) and where
    /// links in that path lead (<paramref name="resolvedTarget"/>). The fix grants the savetether
    /// folder in the same form, so one command covers every mapping of that app.
    /// </summary>
    public SandboxReach? Check(string source, string target, string resolvedTarget, string dataRoot, string resolvedDataRoot)
    {
        if (AppOwning(source) is not { } appId)
        {
            return null;
        }

        var text = _flatpak.ShowPermissions(appId);
        if (text is null)
        {
            return new SandboxReach(appId, null, [], [], null);
        }

        var needed = new List<(string Target, string Grant)> { (target, dataRoot) };
        if (!PathRules.AreSame(target, resolvedTarget))
        {
            needed.Add((resolvedTarget, resolvedDataRoot));
        }

        var filesystems = FlatpakPermissions.ParseFilesystems(text);
        var missing = needed.Where(n => !FlatpakPermissions.CanWrite(filesystems, n.Target, _home, _getVariable)).ToList();
        if (missing.Count == 0)
        {
            return new SandboxReach(appId, true, [], [], null);
        }

        var grants = missing.Select(m => m.Grant).Distinct(StringComparer.Ordinal).ToList();
        return new SandboxReach(appId, false, [.. missing.Select(m => m.Target)], grants, Command(appId, "--filesystem=", grants));
    }

    /// <summary>
    /// Grants the folders <paramref name="reach"/> lists, read-write, for this user only. It changes
    /// that app's settings, so it is only called once the owner has agreed. True when it succeeded.
    /// </summary>
    public bool Grant(SandboxReach reach)
    {
        ArgumentNullException.ThrowIfNull(reach);
        return reach.Grants.Count > 0 && _flatpak.OverrideForUser(reach.AppId, [.. reach.Grants.Select(g => "--filesystem=" + g)]);
    }

    /// <summary>
    /// The grant of the savetether folder (in either form) in the user overrides of the Flatpak app
    /// that holds <paramref name="source"/>; null when there is none. Broader grants such as EmuDeck's
    /// "host" are not SaveTether's and are never reported here.
    /// </summary>
    public SandboxGrant? FindGrant(string source, string dataRoot, string resolvedDataRoot)
    {
        if (AppOwning(source) is not { } appId || _flatpak.ShowUserOverrides(appId) is not { } text)
        {
            return null;
        }

        var overrides = FlatpakPermissions.ParseFilesystems(text);
        var paths = new[] { dataRoot, resolvedDataRoot }
            .Distinct(StringComparer.Ordinal)
            .Where(overrides.ContainsKey)
            .ToList();
        return paths.Count == 0 ? null : new SandboxGrant(appId, paths, Command(appId, "--nofilesystem=", paths));
    }

    /// <summary>Takes a grant back with --nofilesystem, which leaves the app's other overrides alone. True when it succeeded.</summary>
    public bool Revoke(SandboxGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        return _flatpak.OverrideForUser(grant.AppId, [.. grant.Paths.Select(p => "--nofilesystem=" + p)]);
    }

    private static string Command(string appId, string option, IEnumerable<string> paths) =>
        "flatpak override --user " + string.Join(" ", paths.Select(p => option + Quote(p))) + " " + appId;

    /// <summary>Single quotes for the shell when the path holds anything but plain characters.</summary>
    internal static string Quote(string path) =>
        path.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '_' or '-' or '+' or ',' or '@' or '%' or '=')
            ? path
            : "'" + path.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}

/// <summary>Flatpak's filesystem permission rules, as far as SaveTether needs them.</summary>
internal static class FlatpakPermissions
{
    /// <summary>Host folders that "host" does not expose: the sandbox has its own.</summary>
    private static readonly string[] HostReserved =
        ["/app", "/bin", "/dev", "/etc", "/lib", "/lib32", "/lib64", "/proc", "/sbin", "/sys", "/usr", "/tmp", "/run/flatpak", "/run/host"];

    /// <summary>
    /// The "filesystems" entries of the [Context] group, in order, with negations applied: a later
    /// "!entry" removes the same entry, as Flatpak merges overrides. Value: true when read-only.
    /// </summary>
    public static IReadOnlyDictionary<string, bool> ParseFilesystems(string keyFile)
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        var inContext = false;
        foreach (var raw in keyFile.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inContext = line == "[Context]";
                continue;
            }

            if (!inContext || !line.StartsWith("filesystems=", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var item in line["filesystems=".Length..].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var negated = item.StartsWith('!');
                var (entry, readOnly) = SplitMode(negated ? item[1..] : item);
                if (negated)
                {
                    result.Remove(entry);
                }
                else
                {
                    result[entry] = readOnly;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// True when the entry that decides <paramref name="path"/> gives write access. Where several
    /// entries cover it, the most specific one decides, as a deeper mount is placed over a
    /// shallower one: "host;home:ro" leaves home read-only. When unsure, this reading refuses
    /// rather than allows.
    /// </summary>
    public static bool CanWrite(IReadOnlyDictionary<string, bool> filesystems, string path, string home, Func<string, string?> getVariable)
    {
        var covering = filesystems
            .Select(pair => (Root: CoveredRoot(pair.Key, path, home, getVariable), ReadOnly: pair.Value))
            .Where(c => c.Root is not null)
            .Select(c => (Depth: Depth(c.Root!), c.ReadOnly))
            .ToList();
        if (covering.Count == 0)
        {
            return false;
        }

        var deepest = covering.Max(c => c.Depth);
        return covering.Any(c => c.Depth == deepest && !c.ReadOnly);
    }

    private static int Depth(string root) =>
        PathRules.Normalize(root).Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Length;

    private static (string Entry, bool ReadOnly) SplitMode(string item)
    {
        foreach (var (suffix, readOnly) in new[] { (":ro", true), (":rw", false), (":create", false) })
        {
            if (item.EndsWith(suffix, StringComparison.Ordinal))
            {
                return (item[..^suffix.Length], readOnly);
            }
        }

        return (item, false);
    }

    /// <summary>The folder an entry grants when that folder holds <paramref name="path"/>; null when it does not cover it.</summary>
    private static string? CoveredRoot(string entry, string path, string home, Func<string, string?> getVariable)
    {
        string? IfInside(string root) => PathRules.IsSameOrInside(path, root) ? root : null;

        switch (entry)
        {
            case "host":
                return HostReserved.Any(reserved => PathRules.IsSameOrInside(path, reserved)) ? null : Path.GetPathRoot(PathRules.Normalize(path));
            case "host-os" or "host-etc" or "host-root":
                return null;
            case "home" or "~":
                return IfInside(home);
        }

        if (entry.StartsWith("~/", StringComparison.Ordinal))
        {
            return IfInside(Path.Combine(home, entry[2..]));
        }

        // Flatpak paths start with '/'; the second test only matters when tests run on Windows.
        if (entry.StartsWith('/') || Path.IsPathFullyQualified(entry))
        {
            return IfInside(entry);
        }

        if (entry.StartsWith("xdg-", StringComparison.Ordinal))
        {
            var slash = entry.IndexOf('/', StringComparison.Ordinal);
            var name = slash < 0 ? entry : entry[..slash];
            var rest = slash < 0 ? null : entry[(slash + 1)..];
            return XdgFolder(name, home, getVariable) is { } folder ? IfInside(rest is null ? folder : Path.Combine(folder, rest)) : null;
        }

        return null;
    }

    /// <summary>The host folder an xdg-* entry names; null for those that are not under the user's control (xdg-run).</summary>
    private static string? XdgFolder(string name, string home, Func<string, string?> getVariable)
    {
        string Base(string variable, string fallback) =>
            getVariable(variable) is { Length: > 0 } value && value.StartsWith('/') ? value : Path.Combine(home, fallback);

        return name switch
        {
            "xdg-config" => Base("XDG_CONFIG_HOME", ".config"),
            "xdg-data" => Base("XDG_DATA_HOME", Path.Combine(".local", "share")),
            "xdg-cache" => Base("XDG_CACHE_HOME", ".cache"),
            "xdg-desktop" => UserDir("DESKTOP", "Desktop", home, getVariable),
            "xdg-documents" => UserDir("DOCUMENTS", "Documents", home, getVariable),
            "xdg-download" => UserDir("DOWNLOAD", "Downloads", home, getVariable),
            "xdg-music" => UserDir("MUSIC", "Music", home, getVariable),
            "xdg-pictures" => UserDir("PICTURES", "Pictures", home, getVariable),
            "xdg-public-share" => UserDir("PUBLICSHARE", "Public", home, getVariable),
            "xdg-templates" => UserDir("TEMPLATES", "Templates", home, getVariable),
            "xdg-videos" => UserDir("VIDEOS", "Videos", home, getVariable),
            _ => null,
        };
    }

    /// <summary>A user folder from user-dirs.dirs (XDG_&lt;NAME&gt;_DIR="$HOME/..."), else its usual name under home.</summary>
    private static string UserDir(string name, string fallback, string home, Func<string, string?> getVariable)
    {
        var config = getVariable("XDG_CONFIG_HOME") is { Length: > 0 } value && value.StartsWith('/') ? value : Path.Combine(home, ".config");
        try
        {
            var file = Path.Combine(config, "user-dirs.dirs");
            if (File.Exists(file))
            {
                var key = $"XDG_{name}_DIR=";
                var line = File.ReadLines(file).Select(l => l.Trim()).LastOrDefault(l => l.StartsWith(key, StringComparison.Ordinal));
                if (line is not null)
                {
                    var dir = line[key.Length..].Trim('"');
                    return dir.StartsWith("$HOME/", StringComparison.Ordinal) ? Path.Combine(home, dir["$HOME/".Length..])
                        : dir == "$HOME" ? home
                        : dir;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Fall back to the usual name.
        }

        return Path.Combine(home, fallback);
    }
}
