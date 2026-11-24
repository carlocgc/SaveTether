using System.Text;
using SaveTether.Core.FileSystem;
using SaveTether.Core.Model;
using SaveTether.Core.Operations;
using SaveTether.Core.Planning;
using SaveTether.Core.Storage;

namespace SaveTether.Gui.Presentation;

/// <summary>A field of the hand form, so a problem can be shown beside it.</summary>
public enum ManualField
{
    SaveFolder,
    AppName,
    FolderName,
    ProcessNames,
    EntryDepth,
}

/// <summary>What the owner typed. In join mode the app and folder names are the mapping's and are not edited.</summary>
/// <param name="ProcessNames">Process names, separated by commas, semicolons or new lines.</param>
public sealed record ManualAddInput(string? SaveFolder, string? AppName, string? FolderName, string? ProcessNames, int? EntryDepth);

/// <summary>The form as it starts: empty for a new folder, or filled from the mapping for join mode.</summary>
/// <param name="JoinId">The mapping another device made, in join mode; null for a new one.</param>
/// <param name="Target">In join mode, the mapping's shared folder.</param>
/// <param name="SetUpOn">In join mode, the devices that have a folder set for it.</param>
public sealed record ManualAddStart(string Title, IReadOnlyList<string> Sentences, ManualAddInput Input, string? JoinId, string? Target, IReadOnlyList<string> SetUpOn)
{
    public bool IsJoin => JoinId is not null;

    /// <summary>For a folder of an app with a definition (Add app's Custom folder): its ID, which the mapping takes; the app name is then not edited.</summary>
    public string? AppId { get; init; }
}

/// <summary>The form checked against the shared folder: the mapping it makes, and anything in the way.</summary>
/// <param name="JoinsExisting">A new folder whose name another device already uses, with the same shared folder: Add shares it from here too.</param>
/// <param name="ExistingDepth">The entry depth of the mapping joined; the form's own is then not used.</param>
public sealed record ManualAddCheck(
    string? MappingId,
    string? Target,
    IReadOnlyList<(ManualField Field, string Problem)> Problems,
    IReadOnlyList<string> Notes,
    bool JoinsExisting,
    int? ExistingDepth)
{
    public bool CanAdd => MappingId is not null && Target is not null && Problems.Count == 0;
}

/// <summary>What Add did: the mapping added, or why nothing was written.</summary>
public sealed record ManualAddResult(string? Added, string? Refused, ManualAddCheck? Check)
{
    public bool Succeeded => Added is not null;
}

/// <summary>
/// "Add a folder by hand" (desktop-app-spec.md, section 8): the core's <c>map add</c> for an app with no
/// definition. A new folder gets its mapping ID and shared folder from two names in words, so the owner never
/// types an ID; a mapping made on another device (join mode) needs only this device's folder. Checking reads
/// only; Add takes the operation lock, checks again and writes. Nothing is linked: Apply does that.
/// </summary>
public sealed class ManualAddFlow(LocalStore local)
{
    private const int MaxAppIdLength = 60;
    private const int MaxRoleLength = 39;

    private string? _joinId;
    private string? _appId;

    /// <summary>The form for a new folder.</summary>
    public ManualAddStart StartNew()
    {
        _joinId = null;
        _appId = null;
        return new ManualAddStart(
            "Add a folder by hand",
            ["For an app not in the list: any folder it keeps, saves, mods or anything else."],
            new ManualAddInput(null, null, null, null, 1),
            null,
            null,
            []);
    }

    /// <summary>The form for one mapping: this device's folder for it, new or again, given by hand.</summary>
    public ManualAddStart StartJoin(string mappingId)
    {
        ArgumentNullException.ThrowIfNull(mappingId);
        var workspace = Open();
        var mapping = workspace.Shared.TryReadMapping(mappingId)
            ?? throw new StoreException(StoreProblem.NotFound, $"There is no mapping '{mappingId}' any more.");
        var own = workspace.Shared.TryReadDevice(workspace.DeviceName)?.Paths.GetValueOrDefault(mappingId);
        var setUpOn = workspace.Shared.ListDeviceNames()
            .Where(name => name != workspace.DeviceName && HasFolderFor(workspace, name, mappingId))
            .Order(StringComparer.Ordinal)
            .ToList();
        _joinId = mappingId;
        _appId = null;

        var sentences = new List<string>
        {
            own is null
                ? $"Give the folder on this device that {mappingId} shares."
                : $"Nothing is at {own.Source} any more. Give where this folder is on this device now.",
        };
        if (setUpOn.Count > 0)
        {
            sentences.Add($"Set up on {string.Join(", ", setUpOn)}.");
        }

        var slash = mapping.Target.IndexOf('/', StringComparison.Ordinal);
        // The app's name as the Apps page shows it, for the dialogs after Add: a definition's own ("DuckStation"), else the ID's app part.
        var name = AppNames.Resolve(workspace.Definitions, mappingId.Split('.', 2)[0]).Name;
        return new ManualAddStart(
            $"Folder for {mappingId}",
            sentences,
            new ManualAddInput(own?.Source, name, mapping.Target[(slash + 1)..], string.Join(", ", own?.ProcessNames ?? []), mapping.Depth),
            mappingId,
            mapping.Target,
            setUpOn);
    }

    /// <summary>
    /// The form for a folder of an app with a definition, from Add app's Custom folder: one the definition does not list, or
    /// one kept elsewhere on this device. It is shared under the app's own ID, with the folder name the owner gives.
    /// </summary>
    public ManualAddStart StartForApp(string appId, string appName)
    {
        ArgumentNullException.ThrowIfNull(appId);
        ArgumentNullException.ThrowIfNull(appName);
        _joinId = null;
        _appId = appId;
        return new ManualAddStart(
            $"{appName} Add Custom Folder",
            [],
            new ManualAddInput(null, appName, null, null, 1),
            null,
            null,
            []) { AppId = appId };
    }

    /// <summary>
    /// The mapping ID and shared folder a new folder gets, from the names typed: "Dolphin" and "GC"
    /// give dolphin.gc, shared as dolphin/GC. Null where a name gives nothing usable.
    /// </summary>
    /// <param name="appId">The app's ID when it is fixed (a folder of an app with a definition); the app name is then not used.</param>
    public static (string? MappingId, string? Target) Derive(string? appName, string? folderName, string? appId = null)
    {
        var app = appId ?? Slug(appName, MaxAppIdLength);
        var folder = folderName?.Trim();
        var role = Slug(folder, MaxRoleLength);
        return app is null || role is null || string.IsNullOrEmpty(folder) ? (null, null) : ($"{app}.{role}", $"{app}/{folder}");
    }

    /// <summary>The shared folder's name to suggest for a save folder: its own name.</summary>
    public static string? SuggestFolderName(string? saveFolder)
    {
        var trimmed = saveFolder?.Trim().TrimEnd('/', '\\');
        return string.IsNullOrEmpty(trimmed) ? null : Path.GetFileName(trimmed);
    }

    /// <summary>
    /// The shared folder's name after the folder to share changed: the folder's own name while the field is empty or still holds
    /// the last suggestion, so it follows the folder as it is typed; what the owner typed is kept. Found on the Deck:
    /// suggesting only into an empty field left the name at "h", from the first two characters typed.
    /// </summary>
    public static string? FollowFolder(string? current, string? lastSuggested, string? saveFolder)
    {
        var owned = !string.IsNullOrEmpty(current) && !string.Equals(current, lastSuggested, StringComparison.Ordinal);
        return owned ? current : SuggestFolderName(saveFolder) ?? string.Empty;
    }

    /// <summary>"Shared as dolphin/GC (mapping dolphin.gc)."</summary>
    /// <param name="appFixed">The app's ID is fixed (Add app's Custom folder), so only the folder's name is asked for.</param>
    public static string SharedAs(string? mappingId, string? target, bool appFixed = false) =>
        mappingId is not null ? $"Shared as {target} (mapping {mappingId})."
        : appFixed ? "Give the folder a name to see where it is shared." : "Give both names to see where it is shared.";

    /// <summary>Process names from the field: separated by commas, semicolons or new lines, each trimmed, empty ones dropped.</summary>
    public static IReadOnlyList<string> SplitProcessNames(string? text) =>
        (text ?? string.Empty).Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>Checks the form against the rules and the shared folder. Reads only.</summary>
    public ManualAddCheck Check(ManualAddInput input) => Check(Open(), input);

    /// <summary>Add: under the operation lock, the form is checked again and <c>map add</c> runs.</summary>
    /// <param name="joinAccepted">The owner agreed to share an existing shared folder of the same name from here too.</param>
    public ManualAddResult Add(ManualAddInput input, bool joinAccepted)
    {
        ArgumentNullException.ThrowIfNull(input);
        var (session, refused) = OperationSession.Begin(local, "map add", NoProcesses.Instance, flatpak: null);
        if (session is null)
        {
            return new ManualAddResult(null, refused, null);
        }

        using (session)
        {
            try
            {
                var check = Check(session.Workspace, input);
                if (!check.CanAdd || (check.JoinsExisting && !joinAccepted))
                {
                    return new ManualAddResult(null, null, check);
                }

                var join = _joinId is not null || check.JoinsExisting;
                session.Workspace.MapAdd(
                    check.MappingId!,
                    join ? null : check.Target,
                    ExpandHome(input.SaveFolder!.Trim()),
                    SplitProcessNames(input.ProcessNames),
                    join ? null : input.EntryDepth);
                return new ManualAddResult(check.MappingId, null, null);
            }
            catch (StoreException e)
            {
                return new ManualAddResult(null, e.Conflict is { } facts ? AddAppFlow.Conflict(facts, e.Message) : e.Message, null);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new ManualAddResult(null, e.Message, null);
            }
        }
    }

    /// <summary>The question asked before a new folder joins a shared folder another device made under the same name.</summary>
    public static string JoinQuestion(ManualAddCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        return $"Another device already shares {check.Target}. Share the same folder from this device too? "
            + "Apply then compares the two before anything is replaced.";
    }

    private ManualAddCheck Check(Workspace workspace, ManualAddInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var problems = new List<(ManualField, string)>();
        var notes = new List<string>();
        string? id;
        string? target;
        var joinsExisting = false;
        int? existingDepth = null;

        if (_joinId is not null)
        {
            id = _joinId;
            var mapping = workspace.Shared.TryReadMapping(id);
            if (mapping is null)
            {
                problems.Add((ManualField.SaveFolder, $"There is no mapping '{id}' any more."));
                target = null;
            }
            else
            {
                target = mapping.Target;
                existingDepth = mapping.Depth;
            }
        }
        else
        {
            if (_appId is null && Slug(input.AppName, MaxAppIdLength) is null)
            {
                problems.Add((ManualField.AppName, "Give the app a name with at least one letter from a to z, or a digit."));
            }

            if (string.IsNullOrWhiteSpace(input.FolderName))
            {
                problems.Add((ManualField.FolderName, "Give the shared folder a name, for example the folder's own name."));
            }
            else if (Slug(input.FolderName, MaxRoleLength) is null)
            {
                problems.Add((ManualField.FolderName, "The name needs at least one letter from a to z, or a digit."));
            }

            (id, target) = Derive(input.AppName, input.FolderName, _appId);
            if (id is not null && Identifiers.DescribeTargetProblem(id, target) is { } targetProblem)
            {
                problems.Add((ManualField.FolderName, TargetWords(targetProblem)));
            }
            else if (id is not null && workspace.Shared.TryReadMapping(id) is { } existing)
            {
                if (!string.Equals(existing.Target, target, StringComparison.Ordinal))
                {
                    problems.Add((ManualField.FolderName, $"{id} already exists, shared as {existing.Target}. Use another name, or that folder's row."));
                }
                else if (workspace.Shared.TryReadDevice(workspace.DeviceName)?.Paths.GetValueOrDefault(id) is { } own)
                {
                    problems.Add((ManualField.FolderName, $"This device already has a folder for {id}, at {own.Source}. Use its row on the Apps page."));
                }
                else
                {
                    joinsExisting = true;
                    existingDepth = existing.Depth;
                    notes.Add($"Another device already shares {target}: this device would share the same folder.");
                }
            }

            if (input.EntryDepth is not { } depth || depth < 1 || depth > MappingFile.MaxEntryDepth)
            {
                problems.Add((ManualField.EntryDepth, $"Give a whole number from 1 to {MappingFile.MaxEntryDepth}."));
            }
        }

        var folder = input.SaveFolder?.Trim();
        if (string.IsNullOrEmpty(folder))
        {
            problems.Add((ManualField.SaveFolder, "Give the folder to share."));
        }
        else if (!Path.IsPathFullyQualified(ExpandHome(folder)))
        {
            problems.Add((ManualField.SaveFolder, $"'{folder}' is not a full path. Give the whole path, or choose the folder."));
        }
        else if (PathRules.IsOnNetworkDrive(ExpandHome(folder)))
        {
            problems.Add((ManualField.SaveFolder, "This folder is on a network drive. On Windows SaveTether can only link a folder on a local drive."));
        }
        else if (!Directory.Exists(ExpandHome(folder)))
        {
            notes.Add("There is no folder there yet. That is fine if the app has not made it yet.");
        }

        foreach (var name in SplitProcessNames(input.ProcessNames))
        {
            if (name.IndexOfAny(['/', '\\']) >= 0)
            {
                problems.Add((ManualField.ProcessNames, $"'{name}' looks like a path. Give the process name only, for example Dolphin."));
            }
        }

        return new ManualAddCheck(id, target, problems, notes, joinsExisting, existingDepth);
    }

    /// <summary>The core's target rules speak of "the target"; the form calls it the saves' name.</summary>
    private static string TargetWords(string problem) =>
        "The name cannot be used: " + problem.Replace("The target's save folder name", "it", StringComparison.Ordinal);

    /// <summary>Lowercase letters and digits, with '-' between words: "Super Mario" gives "super-mario". Null when nothing is left.</summary>
    private static string? Slug(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var c in text.Trim().ToLowerInvariant())
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_')
            {
                builder.Append(c);
            }
            else if (Fold(c) is { } folded)
            {
                builder.Append(folded);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-', '_');
        if (slug.Length > maxLength)
        {
            slug = slug[..maxLength].TrimEnd('-', '_');
        }

        return slug.Length == 0 ? null : slug;
    }

    /// <summary>
    /// A Latin letter with an accent, as its plain letters: "é" gives "e". SaveTether runs with invariant
    /// globalization, where Unicode normalization cannot be relied on to split accents off, so this is a table.
    /// Letters of other scripts give null and act as a word break.
    /// </summary>
    private static string? Fold(char c) => c switch
    {
        'à' or 'á' or 'â' or 'ã' or 'ä' or 'å' or 'ā' or 'ă' or 'ą' => "a",
        'ç' or 'ć' or 'č' => "c",
        'ď' or 'đ' => "d",
        'è' or 'é' or 'ê' or 'ë' or 'ē' or 'ė' or 'ę' or 'ě' => "e",
        'ğ' => "g",
        'ì' or 'í' or 'î' or 'ï' or 'ī' or 'į' or 'ı' => "i",
        'ł' or 'ľ' => "l",
        'ñ' or 'ń' or 'ň' => "n",
        'ò' or 'ó' or 'ô' or 'õ' or 'ö' or 'ø' or 'ō' or 'ő' => "o",
        'ŕ' or 'ř' => "r",
        'ś' or 'š' or 'ş' => "s",
        'ß' => "ss",
        'ť' or 'ţ' => "t",
        'ù' or 'ú' or 'û' or 'ü' or 'ū' or 'ů' or 'ű' => "u",
        'ý' or 'ÿ' => "y",
        'ź' or 'ż' or 'ž' => "z",
        'æ' => "ae",
        'œ' => "oe",
        'þ' => "th",
        _ => null,
    };

    /// <summary>One unreadable device file does not stop the form: that device is just not named.</summary>
    private static bool HasFolderFor(Workspace workspace, string deviceName, string mappingId)
    {
        try
        {
            return workspace.Shared.TryReadDevice(deviceName)?.Paths.ContainsKey(mappingId) == true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private Workspace Open() => Workspace.Open(local, NoProcesses.Instance);

    private static string ExpandHome(string path) =>
        !OperatingSystem.IsWindows() && (path == "~" || path.StartsWith("~/", StringComparison.Ordinal))
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..]
            : path;
}
