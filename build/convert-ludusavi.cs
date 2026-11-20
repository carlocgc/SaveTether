#:package YamlDotNet@16.3.0
#:project ../src/SaveTether.Core/SaveTether.Core.csproj
#:property PublishAot=false

// Turns the Ludusavi manifest into SaveTether definitions (plan phase 3.5, task T2; design 5.2).
//
//   dotnet run build/convert-ludusavi.cs -- <manifest.yaml> <out.jsonl.gz> [--report <file>] [--commit <sha>] [--verified <folder>]
//   dotnet run build/convert-ludusavi.cs -- --self-test [--bless]
//
// The output is a gzip-compressed JSON Lines file, one definition per line, sorted by ID: the "bundle" that
// DefinitionLoader.ParseBundle reads. Every definition is read back through DefinitionLoader, and one refusal
// fails the run. Run only in CI and by the owner; neither shipped program uses YamlDotNet.
//
// The rules are in docs/plans/phase-3.5-ludusavi-definitions.md, "Conversion rules". Where the plan leaves a
// choice, the choice made here is written beside the code that makes it, and in the plan's "As built" note.

using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using SaveTether.Core.Definitions;
using SaveTether.Core.Model;
using SaveTether.Core.Resolution;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

return Cli.Run(args);

static class Cli
{
    public static int Run(string[] args)
    {
        try
        {
            if (args.Contains("--self-test"))
            {
                return SelfTest.Run(args.Contains("--bless"), Option(args, "--fixtures") ?? "build/ludusavi-fixtures");
            }

            var positional = Positional(args);
            if (positional.Count != 2)
            {
                Console.Error.WriteLine("usage: convert-ludusavi <manifest.yaml> <out.jsonl.gz> [--report <file>] [--commit <sha>] [--verified <folder>]");
                Console.Error.WriteLine("       convert-ludusavi --self-test [--bless]");
                return 2;
            }

            var verified = Converter.VerifiedIds(Option(args, "--verified") ?? "definitions");
            using var reader = File.OpenText(positional[0]);
            var games = Converter.ReadManifest(reader);
            var result = new Converter(verified, Exclusions.Known).Convert(games);

            var errors = Converter.Check(result);
            if (errors.Count > 0)
            {
                Console.Error.WriteLine($"{errors.Count} definition(s) were refused by DefinitionLoader. Fix the rule, or add the game to Exclusions.Known with a reason:");
                foreach (var error in errors)
                {
                    Console.Error.WriteLine("  " + error);
                }

                return 1;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(positional[1]))!);
            File.WriteAllBytes(positional[1], Converter.Bundle(result));

            if (Option(args, "--commit") is { } commit)
            {
                var folder = Path.GetDirectoryName(Path.GetFullPath(positional[1]))!;
                File.WriteAllText(
                    Path.Combine(folder, "ludusavi.source.txt"),
                    $"Ludusavi manifest, https://github.com/mtkennerly/ludusavi-manifest\ncommit {commit}\n");
            }

            var report = result.Report();
            Console.WriteLine(report);
            if (Option(args, "--report") is { } reportPath)
            {
                File.WriteAllText(reportPath, report);
            }

            return 0;
        }
        catch (Exception ex) when (ex is IOException or YamlDotNet.Core.YamlException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static string? Option(string[] args, string name)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    private static List<string> Positional(string[] args)
    {
        var withValue = new[] { "--report", "--commit", "--verified", "--fixtures" };
        var list = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (withValue.Contains(args[i]))
            {
                i++;
            }
            else if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                list.Add(args[i]);
            }
        }

        return list;
    }
}

/// <summary>Games left out by name, each with the reason. Filled in from real runs: a game DefinitionLoader refuses goes here.</summary>
static class Exclusions
{
    public static readonly IReadOnlyDictionary<string, string> Known = new Dictionary<string, string>(StringComparer.Ordinal)
    {
    };
}

// ---- The manifest, as YamlDotNet reads it. Only what the rules use; everything else is ignored. ----

sealed class GameDto
{
    public string? Alias { get; set; }

    public Dictionary<string, FileDto?>? Files { get; set; }

    public SteamDto? Steam { get; set; }

    public Dictionary<string, bool>? Cloud { get; set; }
}

sealed class FileDto
{
    public List<string>? Tags { get; set; }

    public List<WhenDto>? When { get; set; }
}

sealed class WhenDto
{
    public string? Os { get; set; }

    public string? Store { get; set; }
}

sealed class SteamDto
{
    public long? Id { get; set; }
}

// ---- What a path becomes. ----

/// <summary>One folder to link. Family: windows, linux, flatpak or install. Token: the base token ("appdata"); FlatpakId is set for a Flatpak. Steam: false when the manifest limits the folder to other stores.</summary>
sealed record Folder(string Family, string Token, string FlatpakId, string Rel, int Depth, bool Config, bool Account, bool Steam = true)
{
    public string Key => $"{Family}|{Token}|{FlatpakId}|{Rel}".ToLowerInvariant();
}

sealed record InstallOut(string Id, string[] Os, string DetectDir, string? BasePlain, string? BaseWinPath, long? BaseSteamPrefix);

sealed record RoleOut(string Id, int Depth, bool Config, bool Account, SortedDictionary<string, string> Paths);

sealed class GameOut
{
    public required string Name { get; init; }

    public required string Slug { get; init; }

    public long? Steam { get; init; }

    public required List<string> CloudSaves { get; init; }

    public required List<InstallOut> Installs { get; init; }

    public required List<RoleOut> Roles { get; init; }

    public string Id { get; set; } = "";
}

sealed class ConversionResult
{
    public List<(string Id, string Name, string Json)> Definitions { get; } = [];

    public SortedDictionary<string, int> Stats { get; } = new(StringComparer.Ordinal);

    public List<string> Notes { get; } = [];

    public void Bump(string key, int by = 1) => Stats[key] = Stats.GetValueOrDefault(key) + by;

    public string Jsonl() => string.Concat(Definitions.Select(d => d.Json + "\n"));

    public string Report()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Ludusavi manifest conversion");
        foreach (var (key, value) in Stats)
        {
            sb.AppendLine($"  {key}: {value.ToString("N0", CultureInfo.InvariantCulture)}");
        }

        foreach (var note in Notes)
        {
            sb.AppendLine("  note: " + note);
        }

        return sb.ToString();
    }
}

sealed class Converter(HashSet<string> verifiedIds, IReadOnlyDictionary<string, string> exclusions)
{
    private const int MaxSlug = 48;

    private static readonly Regex FileName = new(@"\.[A-Za-z][A-Za-z0-9]{0,5}$", RegexOptions.Compiled);

    private static readonly Dictionary<string, (string Family, string Token)> Placeholders = new(StringComparer.Ordinal)
    {
        ["<winAppData>"] = ("windows", "appdata"),
        ["<winLocalAppData>"] = ("windows", "localappdata"),
        ["<winLocalAppDataLow>"] = ("windows", "locallow"),
        ["<winDocuments>"] = ("windows", "documents"),
        ["<xdgData>"] = ("linux", "xdg_data"),
        ["<xdgConfig>"] = ("linux", "xdg_config"),
    };

    // The folder of a Windows token inside a Wine prefix's profile (Proton: drive_c/users/steamuser).
    private static readonly Dictionary<string, string> ProtonSub = new(StringComparer.Ordinal)
    {
        ["appdata"] = "AppData/Roaming/",
        ["localappdata"] = "AppData/Local/",
        ["locallow"] = "AppData/LocalLow/",
        ["documents"] = "Documents/",
        ["home"] = "",
    };

    private static readonly Dictionary<string, string> CloudStores = new(StringComparer.Ordinal)
    {
        ["steam"] = "steam",
        ["gog"] = "gog",
        ["epic"] = "epic",
        ["origin"] = "ea",
        ["uplay"] = "ubisoft",
        ["xbox"] = "xbox",
    };

    public static Dictionary<string, GameDto> ReadManifest(TextReader reader)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        return deserializer.Deserialize<Dictionary<string, GameDto>>(reader) ?? [];
    }

    /// <summary>The IDs verified definitions use: the file names in the folder, which the loader requires to match.</summary>
    public static HashSet<string> VerifiedIds(string folder) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*" + AppDefinition.FileExtension)
                .Select(Path.GetFileNameWithoutExtension).OfType<string>().ToHashSet(StringComparer.Ordinal)
            : [];

    public ConversionResult Convert(IReadOnlyDictionary<string, GameDto> games)
    {
        var result = new ConversionResult();
        var perGame = new SortedDictionary<string, List<Folder>>(StringComparer.Ordinal);

        // Pass 1: every game's save folders, and which games claim each one.
        foreach (var (name, game) in games)
        {
            if (game is null || game.Alias is not null)
            {
                continue;
            }

            result.Bump("games in the manifest (not aliases)");
            if (game.Files is null)
            {
                continue;
            }

            var saves = game.Files.Where(f => f.Value?.Tags?.Contains("save") == true).ToList();
            if (saves.Count == 0)
            {
                continue;
            }

            result.Bump("games with a path tagged save");
            if (exclusions.TryGetValue(name, out var why))
            {
                result.Bump("games excluded by name");
                result.Notes.Add($"excluded {name}: {why}");
                continue;
            }

            var folders = new List<Folder>();
            foreach (var (path, entry) in saves)
            {
                result.Bump("paths tagged save");
                var (found, drop) = Classify(path, entry!.Tags!.Contains("config"), OsOf(entry.When), StoresOf(entry.When));
                if (drop is not null)
                {
                    result.Bump("paths left out: " + drop);
                }

                folders.AddRange(found);
            }

            perGame[name] = folders;
        }

        // A folder is claimed by another game when both give the same folder, or when it is an ancestor of the other game's folder.
        var claims = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var ancestors = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (name, folders) in perGame)
        {
            foreach (var folder in folders.Where(f => f.Family != "install"))
            {
                AddTo(claims, folder.Key, name);
                var rel = folder.Rel.ToLowerInvariant();
                for (var cut = rel.LastIndexOf('/'); cut > 0; cut = rel.LastIndexOf('/', cut - 1))
                {
                    AddTo(ancestors, BaseKey(folder) + "|" + rel[..cut], name);
                }
            }
        }

        // Pass 2: per game, drop what is claimed, merge repeats, drop what is inside another kept folder, then lay out installs and roles.
        var outs = new List<GameOut>();
        foreach (var (name, folders) in perGame)
        {
            var kept = new List<Folder>();
            foreach (var folder in folders)
            {
                if (folder.Family != "install" && claims[folder.Key].Count > 1)
                {
                    result.Bump("paths left out: folder claimed by more than one game");
                }
                else if (folder.Family != "install" && ancestors.TryGetValue(BaseKey(folder) + "|" + folder.Rel.ToLowerInvariant(), out var inside) && inside.Any(g => g != name))
                {
                    result.Bump("paths left out: folder holds another game's folder");
                }
                else
                {
                    kept.Add(folder);
                }
            }

            kept = MergeRepeats(kept);
            kept = DropNested(kept, result);
            if (kept.Count == 0)
            {
                continue;
            }

            outs.Add(Layout(name, games[name], kept));
        }

        AssignIds(outs, result);
        foreach (var game in outs.OrderBy(g => g.Id, StringComparer.Ordinal))
        {
            result.Definitions.Add((game.Id, game.Name, Json(game)));
            result.Bump("definitions");
            result.Bump("installs", game.Installs.Count);
            foreach (var install in game.Installs)
            {
                result.Bump("installs of kind " + InstallKind(install.Id));
            }

            if (game.Steam is not null)
            {
                result.Bump("definitions with a Steam app ID");
            }
        }

        return result;
    }

    private static void AddTo(Dictionary<string, HashSet<string>> map, string key, string game)
    {
        if (!map.TryGetValue(key, out var set))
        {
            map[key] = set = new HashSet<string>(StringComparer.Ordinal);
        }

        set.Add(game);
    }

    private static string BaseKey(Folder folder) => $"{folder.Family}|{folder.Token}|{folder.FlatpakId}".ToLowerInvariant();

    // ---- One path ----

    /// <summary>The operating systems a path applies to, from its "when" entries. No entry, or an entry without "os", means any.</summary>
    private static HashSet<string> OsOf(List<WhenDto>? when)
    {
        var all = new HashSet<string>(["windows", "linux", "mac", "dos"], StringComparer.Ordinal);
        if (when is null || when.Count == 0 || when.Any(w => w.Os is null))
        {
            return all;
        }

        return when.Select(w => w.Os!).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The stores a path is limited to, or null for any. An entry without "store" means any.</summary>
    private static HashSet<string>? StoresOf(List<WhenDto>? when)
    {
        if (when is null || when.Count == 0 || when.Any(w => w.Store is null))
        {
            return null;
        }

        return when.Select(w => w.Store!).ToHashSet(StringComparer.Ordinal);
    }

    private static string Normalize(string raw)
    {
        var path = raw.Trim().Replace('\\', '/');
        while (path.Contains("//", StringComparison.Ordinal))
        {
            path = path.Replace("//", "/", StringComparison.Ordinal);
        }

        return path.TrimEnd('/');
    }

    /// <summary>Applies the rules to one path. Returns the folders it gives, or the reason it gives none.</summary>
    public static (List<Folder> Folders, string? Drop) Classify(string raw, bool config, HashSet<string> os, HashSet<string>? stores = null)
    {
        var none = new List<Folder>();
        var windows = os.Contains("windows");
        var linux = os.Contains("linux");
        if (!windows && !linux)
        {
            return (none, "macOS or DOS only");
        }

        var path = Normalize(raw);
        if (path.IndexOfAny(['*', '?', '[']) >= 0)
        {
            return (none, "glob");
        }

        // The manifest does not say whether a path is a file. A last part that ends like a file name is taken for one.
        if (FileName.IsMatch(path))
        {
            return (none, "single file");
        }

        // The manifest reaches a sibling of Roaming, Local or LocalLow by going up; the same folder is the token's own.
        var up = Regex.Match(path, @"^<win(?:LocalAppData|AppData)>/\.\./(Roaming|LocalLow|Local)/", RegexOptions.IgnoreCase);
        if (up.Success)
        {
            var sibling = up.Groups[1].Value.ToLowerInvariant() switch { "roaming" => "<winAppData>", "locallow" => "<winLocalAppDataLow>", _ => "<winLocalAppData>" };
            path = sibling + "/" + path[up.Length..];
        }

        var slash = path.IndexOf('/', StringComparison.Ordinal);
        var head = slash < 0 ? path : path[..slash];
        var rest = slash < 0 ? string.Empty : path[(slash + 1)..];
        var known = head is "<home>" or "<base>" || Placeholders.ContainsKey(head);
        if (!known)
        {
            return (none, head.StartsWith('<') ? "placeholder SaveTether has no token for" : "no placeholder");
        }

        var parts = rest.Length == 0 ? [] : rest.Split('/').ToList();
        var accountAt = parts.IndexOf("<storeUserId>");
        var account = accountAt >= 0;
        var depth = 1;
        if (account)
        {
            // Link the folder above the account folder; entries sit past the account folder, so a device's account does not matter.
            depth = Math.Min(MappingFile.MaxEntryDepth, 1 + Math.Max(parts.Count - accountAt - 1, 1));
            parts = parts[..accountAt];
        }

        if (parts.Any(p => p.Contains('<', StringComparison.Ordinal)))
        {
            return (none, "placeholder SaveTether has no token for");
        }

        if (parts.Count == 0)
        {
            return (none, "token with nothing after it");
        }

        var relative = string.Join('/', parts);
        if (IsGenericFolder(head, relative))
        {
            return (none, "generic folder (My Games, Saved Games, Documents)");
        }

        var steam = stores is null || stores.Contains("steam");
        Folder Make(string family, string token, string flatpak, string rel) => new(family, token, flatpak, rel, depth, config, account, steam);

        if (head == "<base>")
        {
            return (new List<Folder> { Make("install", "install_dir", "", relative) }, null);
        }

        if (head == "<home>")
        {
            if (parts[0] == "Library")
            {
                return (none, "macOS or DOS only");
            }

            if (parts[0] == ".var" && parts.Count >= 2 && parts[1] == "app")
            {
                if (parts.Count == 3)
                {
                    return (none, "token with nothing after it");
                }

                return linux && parts.Count > 3
                    ? (new List<Folder> { Make("flatpak", "", parts[2], string.Join('/', parts.Skip(3))) }, null)
                    : (none, "not for a system SaveTether runs on");
            }

            var folders = new List<Folder>();
            // A folder starting with a dot is a Linux home folder; it is not looked for in a Windows profile.
            // The Windows profile's own folders are written under <home> too; they are the tokens' folders.
            var profile = HomeProfileToken(parts);
            if (profile is { Rel.Length: 0 })
            {
                return (none, "token with nothing after it");
            }

            if (profile is { Token: "documents" } docs && IsGenericFolder("<winDocuments>", docs.Rel))
            {
                return (none, "generic folder (My Games, Saved Games, Documents)");
            }

            if (windows && !parts[0].StartsWith('.'))
            {
                folders.Add(profile is { } p ? Make("windows", p.Token, "", p.Rel) : Make("windows", "home", "", relative));
            }

            // AppData is not a Linux folder; Documents may be.
            if (linux && profile is not { Token: not "documents" })
            {
                folders.Add(Make("linux", "home", "", relative));
            }

            return folders.Count > 0 ? (folders, null) : (none, "not for a system SaveTether runs on");
        }

        var (family, token) = Placeholders[head];
        var wanted = family == "windows" ? windows : linux;
        return wanted
            ? (new List<Folder> { Make(family, token, "", relative) }, null)
            : (none, "not for a system SaveTether runs on");
    }

    private static readonly Dictionary<string, string> ProfileTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Roaming"] = "appdata",
        ["Local"] = "localappdata",
        ["LocalLow"] = "locallow",
    };

    /// <summary>The token and rest of a path under AppData/Roaming, AppData/Local, AppData/LocalLow or Documents; null for any other.</summary>
    private static (string Token, string Rel)? HomeProfileToken(List<string> parts)
    {
        if (parts.Count >= 2 && parts[0].Equals("AppData", StringComparison.OrdinalIgnoreCase) && ProfileTokens.TryGetValue(parts[1], out var token))
        {
            return (token, string.Join('/', parts.Skip(2)));
        }

        if (parts.Count >= 2 && parts[0].Equals("Documents", StringComparison.OrdinalIgnoreCase))
        {
            return ("documents", string.Join('/', parts.Skip(1)));
        }

        return null;
    }

    private static bool IsGenericFolder(string head, string relative)
    {
        var lower = relative.ToLowerInvariant();
        return head switch
        {
            "<winDocuments>" => lower is "my games" or "saved games",
            "<home>" => lower is "documents" or "my documents" or "saved games" or "my games" or "documents/my games",
            _ => false,
        };
    }

    /// <summary>The same folder given twice (or in two cases) becomes one; the deeper entry depth and the config mark win.</summary>
    private static List<Folder> MergeRepeats(List<Folder> folders) =>
        folders.GroupBy(f => f.Key)
            .Select(g =>
            {
                var first = g.OrderBy(f => f.Rel, StringComparer.Ordinal).First();
                return first with { Depth = g.Max(f => f.Depth), Config = g.Any(f => f.Config), Account = g.Any(f => f.Account), Steam = g.Any(f => f.Steam) };
            })
            .ToList();

    /// <summary>A folder inside another kept folder of the same game (and base) is dropped: the outer link covers it.</summary>
    private static List<Folder> DropNested(List<Folder> folders, ConversionResult result)
    {
        var kept = new List<Folder>();
        foreach (var folder in folders.OrderBy(f => f.Family, StringComparer.Ordinal).ThenBy(f => f.Token, StringComparer.Ordinal)
                     .ThenBy(f => f.FlatpakId, StringComparer.Ordinal).ThenBy(f => f.Rel, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(f => f.Rel, StringComparer.Ordinal))
        {
            var at = kept.FindIndex(k => k.Family == folder.Family && k.Token == folder.Token && k.FlatpakId == folder.FlatpakId
                && folder.Rel.StartsWith(k.Rel + "/", StringComparison.OrdinalIgnoreCase));
            if (at >= 0)
            {
                result.Bump("paths left out: inside another kept folder of the game");

                // The outer link now covers the inner folder: it takes on the inner folder's marks and an entry depth that reaches its entries (when it has one of its own).
                var outer = kept[at];
                var between = folder.Rel[(outer.Rel.Length + 1)..].Split('/').Length;
                kept[at] = outer with
                {
                    Depth = folder.Depth > 1 ? Math.Max(outer.Depth, Math.Min(MappingFile.MaxEntryDepth, between + folder.Depth)) : outer.Depth,
                    Config = outer.Config || folder.Config,
                    Account = outer.Account || folder.Account,
                    Steam = outer.Steam || folder.Steam,
                };
            }
            else
            {
                kept.Add(folder);
            }
        }

        return kept;
    }

    // ---- One game ----

    private static GameOut Layout(string name, GameDto game, List<Folder> folders)
    {
        var steam = game.Steam?.Id is > 0 ? game.Steam.Id : null;
        var installs = new List<InstallOut>();
        var roles = new List<RoleOut>();

        List<Folder> Of(string family) => folders.Where(f => f.Family == family)
            .OrderBy(f => f.Rel, StringComparer.Ordinal).ThenBy(f => f.Token, StringComparer.Ordinal).ToList();

        // Windows folders: one role per folder, shared by the Windows install and the Proton install, so a save is one copy on both.
        // A game with folders under two Windows tokens gets one install per token, since an install has one base.
        var win = Of("windows");
        if (win.Count > 0)
        {
            var tokens = win.Select(f => f.Token).Distinct().Order(StringComparer.Ordinal).ToList();
            string Suffix(string token) => tokens.Count > 1 ? "-" + token.Replace('_', '-') : "";
            foreach (var token in tokens)
            {
                var suffix = Suffix(token);
                var first = win.First(f => f.Token == token).Rel;
                installs.Add(new InstallOut("windows" + suffix, ["windows"], $"{{{token}}}/{first}", $"{{{token}}}", null, null));

                // The Proton install is detected by a folder that is Steam's; a folder the manifest gives only for other stores does not show one.
                var steamFirst = win.FirstOrDefault(f => f.Token == token && f.Steam);
                if (steam is not null && steamFirst is not null)
                {
                    installs.Add(new InstallOut(
                        "steam-proton" + suffix,
                        ["linux"],
                        $"{{steam_compatdata:{steam}}}/pfx/drive_c/users/steamuser/{ProtonSub[token]}{steamFirst.Rel}",
                        null,
                        $"{{{token}}}",
                        steam));
                }
            }

            AddRoles(
                roles,
                "save",
                win,
                f => steam is null || !f.Steam ? ["windows" + Suffix(f.Token)] : ["windows" + Suffix(f.Token), "steam-proton" + Suffix(f.Token)]);
        }

        var linuxFolders = Of("linux");
        if (linuxFolders.Count > 0)
        {
            var tokens = linuxFolders.Select(f => f.Token).Distinct().Order(StringComparer.Ordinal).ToList();
            foreach (var token in tokens)
            {
                var suffix = tokens.Count > 1 ? "-" + token.Replace('_', '-') : "";
                installs.Add(new InstallOut("linux" + suffix, ["linux"], $"{{{token}}}/{linuxFolders.First(f => f.Token == token).Rel}", $"{{{token}}}", null, null));
            }

            AddRoles(roles, "linux-save", linuxFolders, f => [tokens.Count > 1 ? "linux-" + f.Token.Replace('_', '-') : "linux"]);
        }

        var flatpaks = Of("flatpak");
        if (flatpaks.Count > 0)
        {
            foreach (var id in flatpaks.Select(f => f.FlatpakId).Distinct().Order(StringComparer.Ordinal))
            {
                installs.Add(new InstallOut("flatpak-" + Slug(id), ["linux"], $"{{flatpak:{id}}}/{flatpaks.First(f => f.FlatpakId == id).Rel}", $"{{flatpak:{id}}}", null, null));
            }

            // Flatpaks of one game are the same app packaged twice: the nth folder of each is the same role.
            foreach (var group in flatpaks.GroupBy(f => f.FlatpakId))
            {
                var index = 0;
                foreach (var folder in group)
                {
                    index++;
                    var roleId = RoleId("flatpak-save", index);
                    var at = roles.FindIndex(r => r.Id == roleId);
                    var existing = at >= 0 ? roles[at] : new RoleOut(roleId, 1, false, false, new SortedDictionary<string, string>(StringComparer.Ordinal));
                    existing.Paths["flatpak-" + Slug(folder.FlatpakId)] = folder.Rel;
                    var merged = existing with { Depth = Math.Max(existing.Depth, folder.Depth), Config = existing.Config || folder.Config, Account = existing.Account || folder.Account };
                    if (at >= 0)
                    {
                        roles[at] = merged;
                    }
                    else
                    {
                        roles.Add(merged);
                    }
                }
            }

        }

        var inDir = Of("install");
        if (inDir.Count > 0)
        {
            installs.Add(new InstallOut("install-dir", ["windows", "linux"], $"{{install_dir}}/{inDir[0].Rel}", "{install_dir}", null, null));
            AddRoles(roles, "install-save", inDir, _ => ["install-dir"]);
        }

        var cloud = (game.Cloud ?? [])
            .Where(c => c.Value && CloudStores.ContainsKey(c.Key))
            .Select(c => CloudStores[c.Key]).Distinct().Order(StringComparer.Ordinal).ToList();

        return new GameOut { Name = name, Slug = Slug(name), Steam = steam, CloudSaves = cloud, Installs = installs, Roles = roles };
    }

    private static string RoleId(string prefix, int index) => index == 1 ? prefix : $"{prefix}-{index}";

    private static void AddRoles(List<RoleOut> roles, string prefix, List<Folder> folders, Func<Folder, IEnumerable<string>> installsFor)
    {
        var index = 0;
        foreach (var folder in folders)
        {
            index++;
            var paths = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var install in installsFor(folder))
            {
                paths[install] = folder.Rel;
            }

            roles.Add(new RoleOut(RoleId(prefix, index), folder.Depth, folder.Config, folder.Account, paths));
        }
    }

    private static string InstallKind(string id) =>
        id.StartsWith("steam-proton", StringComparison.Ordinal) ? "steam-proton"
        : id.StartsWith("windows", StringComparison.Ordinal) ? "windows"
        : id.StartsWith("linux", StringComparison.Ordinal) ? "linux"
        : id.StartsWith("flatpak-", StringComparison.Ordinal) ? "flatpak"
        : id;

    // ---- IDs ----

    public static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsAsciiLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }

        var slug = sb.ToString().Trim('-');
        return slug.Length > MaxSlug ? slug[..MaxSlug].TrimEnd('-') : slug;
    }

    /// <summary>
    /// An ID is a slug of the name. Games with the same slug each get their Steam app ID appended; those without one are
    /// numbered after the first. A slug a verified definition uses drops every game with it.
    /// </summary>
    private void AssignIds(List<GameOut> games, ConversionResult result)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var groups = games.GroupBy(g => g.Slug.Length > 0 ? g.Slug : (g.Steam is null ? "" : $"steam-{g.Steam}"))
            .OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        var plainSlugs = groups.Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var members = group.OrderBy(g => g.Name, StringComparer.Ordinal).ToList();
            if (group.Key.Length == 0)
            {
                result.Bump("games left out: no usable ID", members.Count);
                members.ForEach(m => m.Id = "");
                continue;
            }

            if (verifiedIds.Contains(group.Key))
            {
                result.Bump("games left out: a verified definition has the ID", members.Count);
                members.ForEach(m => m.Id = "");
                continue;
            }

            if (members.Count > 1)
            {
                result.Bump("games whose ID needed the Steam app ID or a number", members.Count);
            }

            var plainTaken = false;
            var number = 1;
            foreach (var member in members)
            {
                var candidate = members.Count == 1 ? group.Key
                    : member.Steam is not null ? $"{group.Key}-{member.Steam}"
                    : !plainTaken ? group.Key
                    : $"{group.Key}-{++number}";
                if (members.Count > 1 && member.Steam is null)
                {
                    plainTaken = true;
                }

                // A generated ID may not be another group's plain slug either, or that group's game would be pushed to a number.
                while (used.Contains(candidate) || (candidate != group.Key && plainSlugs.Contains(candidate)))
                {
                    candidate = $"{candidate}-{++number}";
                }

                used.Add(candidate);
                if (verifiedIds.Contains(candidate))
                {
                    result.Bump("games left out: a verified definition has the ID");
                    member.Id = "";
                    continue;
                }

                member.Id = candidate;
            }
        }

        games.RemoveAll(g => g.Id.Length == 0);
    }

    // ---- Output ----

    private static string Json(GameOut game)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", AppDefinition.CurrentSchema);
            w.WriteString("id", game.Id);
            w.WriteString("name", game.Name);
            w.WriteString("kind", "game");

            w.WriteStartArray("installs");
            foreach (var install in game.Installs)
            {
                w.WriteStartObject();
                w.WriteString("id", install.Id);
                if (install.Os.Length == 1)
                {
                    w.WriteString("os", install.Os[0]);
                }
                else
                {
                    w.WriteStartArray("os");
                    install.Os.ToList().ForEach(w.WriteStringValue);
                    w.WriteEndArray();
                }

                w.WriteStartObject("detect");
                w.WriteString("dir", install.DetectDir);
                w.WriteEndObject();
                if (install.BasePlain is not null)
                {
                    w.WriteString("base", install.BasePlain);
                }
                else
                {
                    w.WriteStartObject("base");
                    w.WriteString("winpath", install.BaseWinPath);
                    w.WriteStartObject("prefix");
                    w.WriteNumber("steam", install.BaseSteamPrefix!.Value);
                    w.WriteEndObject();
                    w.WriteEndObject();
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();

            w.WriteStartArray("roles");
            foreach (var role in game.Roles)
            {
                w.WriteStartObject();
                w.WriteString("id", role.Id);
                w.WriteString("class", "save");
                w.WriteBoolean("default", true);
                w.WriteStartObject("path");
                foreach (var (install, path) in role.Paths)
                {
                    w.WriteString(install, path);
                }

                w.WriteEndObject();
                w.WriteString("target", role.Id);
                if (role.Depth > 1)
                {
                    w.WriteNumber("entry_depth", role.Depth);
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();

            var quirks = new List<string>();
            foreach (var role in game.Roles)
            {
                if (role.Config)
                {
                    quirks.Add($"Role \"{role.Id}\" may also hold settings that should differ per device: the manifest tags the folder config as well as save.");
                }

                if (role.Account)
                {
                    quirks.Add($"Role \"{role.Id}\" holds a folder per store account. Linking it shares every account's saves, whichever accounts a device uses.");
                }
            }

            if (quirks.Count > 0)
            {
                w.WriteStartArray("quirks");
                quirks.ForEach(w.WriteStringValue);
                w.WriteEndArray();
            }

            w.WriteString("tier", "imported");
            w.WriteString("origin", "ludusavi");
            if (game.Steam is not null)
            {
                w.WriteStartObject("store_ids");
                w.WriteNumber("steam", game.Steam.Value);
                w.WriteEndObject();
            }

            if (game.CloudSaves.Count > 0)
            {
                w.WriteStartArray("cloud_saves");
                game.CloudSaves.ForEach(w.WriteStringValue);
                w.WriteEndArray();
            }

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The bundle: gzip-compressed JSON Lines. The same input always gives the same bytes.</summary>
    public static byte[] Bundle(ConversionResult result)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(new UTF8Encoding(false).GetBytes(result.Jsonl()));
        }

        // Byte 9 of the header names the OS that wrote it (zlib: 0x0a on Windows, 0x03 on Linux). 0xff, "unknown", keeps the bytes the same everywhere.
        var bytes = output.ToArray();
        bytes[9] = 0xff;
        return bytes;
    }

    /// <summary>Reads the bundle back through DefinitionLoader. Returns one message per refused definition; empty when all pass.</summary>
    public static List<string> Check(ConversionResult result)
    {
        using var stream = new MemoryStream(Bundle(result));
        var (definitions, errors) = DefinitionLoader.ParseBundle(stream);
        var messages = errors
            .Select(e => e.Line >= 1 && e.Line <= result.Definitions.Count
                ? $"{result.Definitions[e.Line - 1].Id} ({result.Definitions[e.Line - 1].Name}): {e.Message}"
                : e.ToString())
            .ToList();
        if (messages.Count == 0 && definitions.Count != result.Definitions.Count)
        {
            messages.Add($"{result.Definitions.Count} definitions were written, but {definitions.Count} were read back");
        }

        // Tokens are not the loader's to check (design 5.2); the resolver's lists are.
        var plain = PathResolver.PlainTokens;
        var windows = PathResolver.WindowsTokens;
        foreach (var definition in definitions)
        {
            foreach (var install in definition.Installs)
            {
                var token = Token(install.Base.Path ?? install.Base.WinPath);
                if (token is null || !(install.Base.WinPath is not null ? windows : plain).Contains(token))
                {
                    messages.Add($"{definition.Id}: install \"{install.Id}\" has a base token the resolver does not know");
                }
            }
        }

        return messages;
    }

    private static string? Token(string? template) =>
        template is { Length: > 2 } && template[0] == '{' && template.IndexOf('}') is var close and > 0
            ? template[1..close].Split(':')[0]
            : null;
}

// ---- --self-test: one fixture folder per rule ----

static class SelfTest
{
    // Each folder holds manifest.yaml and expected.jsonl; optionally verified.txt (IDs of verified definitions)
    // and expect-fail.txt (text the refusal must contain, instead of expected.jsonl).
    public static int Run(bool bless, string fixtures)
    {
        if (!Directory.Exists(fixtures))
        {
            Console.Error.WriteLine($"no fixtures at {fixtures}");
            return 1;
        }

        var failures = 0;
        var cases = Directory.EnumerateDirectories(fixtures).Order(StringComparer.Ordinal).ToList();
        foreach (var folder in cases)
        {
            var name = Path.GetFileName(folder);
            var verified = File.Exists(Path.Combine(folder, "verified.txt"))
                ? File.ReadAllLines(Path.Combine(folder, "verified.txt")).Where(l => l.Length > 0).ToHashSet(StringComparer.Ordinal)
                : [];
            using var reader = File.OpenText(Path.Combine(folder, "manifest.yaml"));
            var result = new Converter(verified, Exclusions.Known).Convert(Converter.ReadManifest(reader));
            var errors = Converter.Check(result);
            var failFile = Path.Combine(folder, "expect-fail.txt");

            if (File.Exists(failFile))
            {
                var expected = File.ReadAllText(failFile).Trim();
                var ok = errors.Any(e => e.Contains(expected, StringComparison.Ordinal));
                failures += Report(name, ok, ok ? "" : $"expected a refusal containing \"{expected}\", got: {string.Join(" | ", errors)}");
                continue;
            }

            if (errors.Count > 0)
            {
                failures += Report(name, false, "the loader refused: " + string.Join(" | ", errors));
                continue;
            }

            var actual = result.Jsonl();
            var expectedPath = Path.Combine(folder, "expected.jsonl");
            if (bless)
            {
                File.WriteAllText(expectedPath, actual, new UTF8Encoding(false));
            }

            var want = File.Exists(expectedPath) ? File.ReadAllText(expectedPath) : null;
            failures += Report(name, want == actual, want is null ? "no expected.jsonl (run with --bless and read the result)" : "output differs from expected.jsonl");
        }

        // The same input must give the same bytes, or the weekly pull request would never be quiet.
        var sample = Path.Combine(cases.First(c => File.Exists(Path.Combine(c, "expected.jsonl"))), "manifest.yaml");
        byte[] Once()
        {
            using var reader = File.OpenText(sample);
            return Converter.Bundle(new Converter([], Exclusions.Known).Convert(Converter.ReadManifest(reader)));
        }

        var one = Once();
        var two = Once();
        failures += Report("same input, same bytes", one.AsSpan().SequenceEqual(two), "two runs gave different bytes");

        Console.WriteLine(failures == 0 ? $"self-test passed ({cases.Count} fixtures)" : $"self-test FAILED ({failures})");
        return failures == 0 ? 0 : 1;
    }

    private static int Report(string name, bool ok, string problem)
    {
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {name}{(ok ? "" : ": " + problem)}");
        return ok ? 0 : 1;
    }
}
