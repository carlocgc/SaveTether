using System.Text.Json.Serialization;
using SaveTether.Core.Definitions;
using SaveTether.Core.FileSystem;
using SaveTether.Core.Model;
using SaveTether.Core.Resolution;
using SaveTether.Core.Storage;

namespace SaveTether.Core.Operations;

/// <summary>What "app add" was asked to do. Null values fall back to what this device recorded last time.</summary>
/// <param name="Install">The install ID to use, or null to detect it.</param>
/// <param name="Roles">The role IDs to add, or empty for the roles the definition turns on by default.</param>
public sealed record AppAddRequest(
    string AppId,
    string? Install = null,
    IReadOnlyList<string>? Roles = null,
    string? InstallDir = null,
    string? SdCard = null,
    string? WinePrefix = null);

/// <summary>Why "app add" has no plan.</summary>
public enum AppAddProblem
{
    /// <summary>No definition has that ID.</summary>
    [JsonStringEnumMemberName("unknown-app")]
    UnknownApp,

    /// <summary>The definition file has mistakes.</summary>
    [JsonStringEnumMemberName("invalid-definition")]
    InvalidDefinition,

    /// <summary>The install asked for is not in the definition, or not for this operating system.</summary>
    [JsonStringEnumMemberName("unknown-install")]
    UnknownInstall,

    /// <summary>No install of the app was found on this device.</summary>
    [JsonStringEnumMemberName("not-installed")]
    NotInstalled,

    /// <summary>More than one install was found. The owner must choose; SaveTether does not guess.</summary>
    [JsonStringEnumMemberName("install-choice-needed")]
    InstallChoiceNeeded,

    /// <summary>A role asked for is not in the definition, or the chosen install does not have it.</summary>
    [JsonStringEnumMemberName("unknown-role")]
    UnknownRole,

    /// <summary>A role's folder could not be resolved.</summary>
    [JsonStringEnumMemberName("unresolved")]
    Unresolved,

    /// <summary>The change contradicts what is recorded: another target, or the role is applied elsewhere.</summary>
    [JsonStringEnumMemberName("conflict")]
    Conflict,

    /// <summary>A role's folder is on a network drive, where Windows cannot make a junction.</summary>
    [JsonStringEnumMemberName("not-local")]
    NotLocal,
}

/// <summary>One install of the app, and whether it was found here.</summary>
/// <param name="Found">What showed it is there: a folder, file, Flatpak app or Steam manifest. Null when not found.</param>
/// <param name="Reason">Why it was not found. Null when found.</param>
/// <param name="Problem">Why it was not found, as a kind: missing, needs a value, and so on. Null when found.</param>
public sealed record InstallCandidate(string Id, string? Found, Note? Reason, ResolveProblem? Problem = null)
{
    public bool IsDetected => Found is not null;
}

/// <summary>One role "app add" will turn into a mapping and this device's save folder for it.</summary>
/// <param name="Source">The save folder on this device.</param>
/// <param name="Exists">False when the folder is not there yet; apply then starts it from the shared copy.</param>
/// <param name="IsLink">True when the folder is already a link, so its size is the shared copy's.</param>
/// <param name="Bytes">Total size of the files in the folder, without following links inside it.</param>
/// <param name="CreatesMapping">True when no device has added this mapping yet.</param>
/// <param name="EntryDepth">The mapping's entry depth: the definition's for a new mapping, the existing one's otherwise.</param>
/// <param name="Note">A sentence the owner should read, such as an existing mapping's depth differing from the definition.</param>
public sealed record AppRolePlan(
    string RoleId,
    RoleClass Class,
    string MappingId,
    string Target,
    string Source,
    bool Exists,
    bool IsLink,
    long Bytes,
    int Files,
    bool CreatesMapping,
    int EntryDepth,
    Note? Note);

/// <summary>What "app add" will write, before it writes anything.</summary>
public sealed record AppAddPlan(
    string AppId,
    string AppName,
    DefinitionSource DefinitionSource,
    string DefinitionLocation,
    string Install,
    DeviceApp Values,
    IReadOnlyList<AppRolePlan> Roles,
    IReadOnlyList<string> Quirks)
{
    /// <summary>False when the definition was converted from the Ludusavi manifest and not tried by hand.</summary>
    public bool Verified => DefinitionSource != DefinitionSource.Imported;
}

/// <summary>A plan, or the reason there is none. <see cref="Candidates"/> lists the installs looked at.</summary>
public sealed record AppAddOutcome(
    AppAddPlan? Plan, AppAddProblem? Problem, Note? Message, IReadOnlyList<InstallCandidate> Candidates)
{
    /// <summary>For <see cref="AppAddProblem.Conflict"/>: what is in the way, as fields. Null otherwise.</summary>
    public ConflictFacts? Conflict { get; init; }

    public static AppAddOutcome Refuse(AppAddProblem problem, Note message, IReadOnlyList<InstallCandidate>? candidates = null) =>
        new(null, problem, message, candidates ?? []);

    public static AppAddOutcome RefuseConflict(Note message, ConflictFacts conflict, IReadOnlyList<InstallCandidate> candidates) =>
        Refuse(AppAddProblem.Conflict, message, candidates) with { Conflict = conflict };
}

public sealed partial class Workspace
{
    /// <summary>The catalog this device reads definitions from: the shared folder, then the built-in ones.</summary>
    public DefinitionCatalog Definitions => new(Path.Combine(Shared.MetaRoot, DefinitionCatalog.SharedFolderName));

    /// <summary>
    /// Works out what "app add" would do: which install, which roles, where each save folder is and
    /// how big it is. Reads only. Every reason not to go ahead is found here, before anything is written.
    /// </summary>
    public AppAddOutcome PlanAppAdd(AppAddRequest request, ResolverEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(environment);
        var device = ReadOwnDevice();

        if (!Identifiers.IsValidAppId(request.AppId))
        {
            return AppAddOutcome.Refuse(AppAddProblem.UnknownApp, $"'{request.AppId}' is not a valid app ID.");
        }

        var catalog = Definitions;
        var entry = catalog.Find(request.AppId);
        if (entry is null)
        {
            return AppAddOutcome.Refuse(
                AppAddProblem.UnknownApp,
                $"There is no definition for '{request.AppId}'. Known apps: {string.Join(", ", catalog.AvailableIds(includeImported: false))}. Imported definitions are not listed here.");
        }

        if (entry.Definition is not { } definition)
        {
            return AppAddOutcome.Refuse(
                AppAddProblem.InvalidDefinition,
                $"The definition {entry.Location} has mistakes:\n" + string.Join("\n", entry.Errors));
        }

        var stored = device.Apps?.GetValueOrDefault(definition.Id);
        var values = new DeviceValues(
            InstallDir: Absolute(request.InstallDir) ?? stored?.InstallDir,
            SdCard: Absolute(request.SdCard) ?? stored?.SdCard,
            WinePrefix: Absolute(request.WinePrefix) ?? stored?.WinePrefix);
        var resolver = new PathResolver(environment);

        // Which install: the one asked for, else the only one found, else the one used last time if still found.
        var forThisOs = definition.Installs.Where(i => i.Os.Contains(environment.Os)).ToList();
        var candidates = forThisOs.Select(install =>
        {
            var detected = resolver.Detect(install, values);
            return new InstallCandidate(install.Id, detected.Path, detected.Message, detected.Problem);
        }).ToList();

        InstallDefinition chosen;
        if (request.Install is { } asked)
        {
            chosen = forThisOs.FirstOrDefault(i => i.Id == asked)!;
            if (chosen is null)
            {
                return AppAddOutcome.Refuse(
                    AppAddProblem.UnknownInstall,
                    $"'{definition.Id}' has no install '{asked}' for this system. Its installs here: {Join(forThisOs.Select(i => i.Id))}.",
                    candidates);
            }
        }
        else
        {
            var detected = candidates.Where(c => c.IsDetected).ToList();
            if (detected.Count == 0)
            {
                // An install that could only not be looked for, because it needs a value such as its
                // install folder, is the likely one: say how to give that value.
                var needValues = candidates.Where(c => c.Problem == ResolveProblem.NeedsValue).Select(c => $"'{c.Id}'").ToList();
                var text = $"No install of {definition.Name} was found on this device.";
                var next = new List<NextStep>();
                if (needValues.Count > 0)
                {
                    text += $" {(needValues.Count == 1 ? "Install" : "Installs")} {string.Join(", ", needValues)} can only be looked for once you say where.";
                    next.Add(new NextStep(NextStepKind.GiveLocation));
                }

                // Looked for where it was last time and not found there: it has probably moved.
                if (MovedHint(definition.Name, request, stored) is { } moved)
                {
                    text += moved;
                    next.Add(new NextStep(NextStepKind.GiveNewLocation));
                }

                return AppAddOutcome.Refuse(
                    AppAddProblem.NotInstalled,
                    forThisOs.Count == 0 ? $"'{definition.Id}' has no install for this system." : new Note(text, next),
                    candidates);
            }

            var pick = detected.Count == 1 ? detected[0]
                : detected.FirstOrDefault(c => c.Id == stored?.Install);
            if (pick is null)
            {
                return AppAddOutcome.Refuse(
                    AppAddProblem.InstallChoiceNeeded,
                    new Note(
                        $"{definition.Name} was found more than once on this device ({Join(detected.Select(c => c.Id))}).",
                        new NextStep(NextStepKind.ChooseInstall)),
                    candidates);
            }

            chosen = forThisOs.First(i => i.Id == pick.Id);
        }

        // Which roles: those asked for, else the default ones this install has.
        var roles = new List<RoleDefinition>();
        if (request.Roles is { Count: > 0 } askedRoles)
        {
            foreach (var roleId in askedRoles.Distinct(StringComparer.Ordinal))
            {
                var role = definition.Roles.FirstOrDefault(r => r.Id == roleId);
                if (role is null || !role.Paths.ContainsKey(chosen.Id))
                {
                    var available = definition.Roles.Where(r => r.Paths.ContainsKey(chosen.Id)).Select(r => r.Id);
                    return AppAddOutcome.Refuse(
                        AppAddProblem.UnknownRole,
                        $"Install '{chosen.Id}' of '{definition.Id}' has no role '{roleId}'. Its roles: {Join(available)}.",
                        candidates);
                }

                roles.Add(role);
            }
        }
        else
        {
            roles.AddRange(definition.Roles.Where(r => r.Default && r.Paths.ContainsKey(chosen.Id)));
            if (roles.Count == 0)
            {
                return AppAddOutcome.Refuse(
                    AppAddProblem.UnknownRole,
                    new Note(
                        $"Install '{chosen.Id}' of '{definition.Id}' has no role that is on by default.",
                        new NextStep(NextStepKind.NameRoles)),
                    candidates);
            }
        }

        var planned = new List<AppRolePlan>();
        foreach (var role in roles)
        {
            var resolved = resolver.ResolveRole(chosen, role, values);
            if (!resolved.IsResolved)
            {
                return AppAddOutcome.Refuse(
                    AppAddProblem.Unresolved,
                    new Note(
                        $"The folder for role '{role.Id}' could not be found: {resolved.Message?.Text}",
                        [.. resolved.Message?.Next ?? [], .. NextFor(resolved.Problem)]),
                    candidates);
            }

            var mappingId = role.MappingIdFor(definition.Id);
            var target = role.MappingTargetFor(definition.Id);
            var source = PathRules.Normalize(resolved.Path!);
            if (PathRules.IsOnNetworkDrive(source))
            {
                return AppAddOutcome.Refuse(AppAddProblem.NotLocal, $"Role '{role.Id}': {NetworkFolderMessage(source)}", candidates);
            }

            var mapping = Shared.TryReadMapping(mappingId);
            if (mapping is not null && mapping.Target != target)
            {
                return AppAddOutcome.RefuseConflict(
                    $"Mapping '{mappingId}' already exists with target '{mapping.Target}', but the definition says '{target}'.",
                    new ConflictFacts(ConflictKind.TargetDiffers, mappingId, Target: mapping.Target),
                    candidates);
            }

            if (device.Paths.TryGetValue(mappingId, out var current) && !PathRules.AreSame(current.Source, source) && IsLink(current.Source))
            {
                return AppAddOutcome.RefuseConflict(
                    new Note(
                        $"'{mappingId}' is applied on this device at '{current.Source}', but the definition now finds '{source}'. " +
                        "It has to be reverted before the app is added again.",
                        new NextStep(NextStepKind.Revert, mappingId)),
                    new ConflictFacts(ConflictKind.AppliedElsewhere, mappingId, Folder: current.Source),
                    candidates);
            }

            // Against this device's other mappings, and the roles planned before this one.
            var others = SourcesOf(device).Concat(planned.Select(p => new KeyValuePair<string, string>(p.MappingId, p.Source)));
            if (SourceOverlap(mappingId, source, others) is { } overlap)
            {
                return AppAddOutcome.RefuseConflict(overlap.Message, overlap.Facts, candidates);
            }

            var note = mapping is not null && mapping.Depth != role.EntryDepth
                ? new Note(
                    $"The existing mapping's entry depth ({mapping.Depth}) is kept; the definition says {role.EntryDepth}.",
                    new NextStep(NextStepKind.ChangeEntryDepth, mappingId))
                : null;
            var (exists, isLink, bytes, files) = Measure(source);
            planned.Add(new AppRolePlan(
                role.Id, role.Class, mappingId, target, source, exists, isLink, bytes, files,
                CreatesMapping: mapping is null, EntryDepth: mapping?.Depth ?? role.EntryDepth, note));
        }

        var app = new DeviceApp { Install = chosen.Id, InstallDir = values.InstallDir, SdCard = values.SdCard, WinePrefix = values.WinePrefix };
        return new AppAddOutcome(
            new AppAddPlan(definition.Id, definition.Name, entry.Source, entry.Location, chosen.Id, app, planned, definition.Quirks),
            null, null, candidates);
    }

    /// <summary>
    /// Writes what <see cref="PlanAppAdd"/> planned: each role's mapping (created when new), this
    /// device's save folder for it, and how the app was found. Links nothing; "apply" does that.
    /// </summary>
    public void AddApp(AppAddPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        foreach (var role in plan.Roles)
        {
            var existing = ReadOwnDevice().Paths.GetValueOrDefault(role.MappingId);
            MapAdd(
                role.MappingId,
                role.Target,
                role.Source,
                existing?.ProcessNames ?? [],
                role.CreatesMapping ? role.EntryDepth : null);
        }

        var device = ReadOwnDevice();
        var apps = new Dictionary<string, DeviceApp>(device.Apps ?? new Dictionary<string, DeviceApp>(), StringComparer.Ordinal)
        {
            [plan.AppId] = plan.Values,
        };
        Shared.WriteDevice(device with { Apps = apps });
    }

    private static string? Absolute(string? path) => path is null ? null : Path.GetFullPath(path);

    private static string Join(IEnumerable<string> ids) => ids.Any() ? string.Join(", ", ids) : "none";

    private static NextStep[] NextFor(ResolveProblem? problem) => problem switch
    {
        ResolveProblem.NeedsValue => [new NextStep(NextStepKind.GiveLocation)],
        ResolveProblem.Ambiguous => [new NextStep(NextStepKind.ChooseLocation)],
        _ => [],
    };

    /// <summary>
    /// When nothing was found and a remembered location was used in place of one given now, says
    /// so: the app has most likely moved, and giving its new location is the fix.
    /// </summary>
    /// <returns>The sentences to add, starting with a space, or null when nothing remembered was used.</returns>
    private static string? MovedHint(string appName, AppAddRequest request, DeviceApp? stored)
    {
        var remembered = new List<string>();
        if (request.InstallDir is null && stored?.InstallDir is { } dir)
        {
            remembered.Add($"install folder '{dir}'");
        }

        if (request.SdCard is null && stored?.SdCard is { } card)
        {
            remembered.Add($"SD card '{card}'");
        }

        if (request.WinePrefix is null && stored?.WinePrefix is { } prefix)
        {
            remembered.Add($"Wine prefix '{prefix}'");
        }

        return remembered.Count == 0
            ? null
            : $" It was looked for at the {string.Join(" and the ", remembered)} remembered from last time, so {appName} may have moved.";
    }

    /// <summary>Whether a folder is there, whether it is a link, and the size of what is in it, not following links inside.</summary>
    private static (bool Exists, bool IsLink, long Bytes, int Files) Measure(string folder)
    {
        var kind = SafeFs.Probe(folder);
        var isLink = kind is EntryKind.Symlink or EntryKind.Junction;
        if (kind != EntryKind.Directory && !(isLink && Directory.Exists(folder)))
        {
            return (false, isLink, 0, 0);
        }

        long bytes = 0;
        var files = 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", options))
        {
            bytes += file.Length;
            files++;
        }

        return (true, isLink, bytes, files);
    }
}
