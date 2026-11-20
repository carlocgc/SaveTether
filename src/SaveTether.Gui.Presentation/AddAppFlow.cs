using SaveTether.Core.Definitions;
using SaveTether.Core.Detection;
using SaveTether.Core.Model;
using SaveTether.Core.Operations;
using SaveTether.Core.Planning;
using SaveTether.Core.Resolution;
using SaveTether.Core.Storage;

namespace SaveTether.Gui.Presentation;

/// <summary>What page 1 shows next to an app (desktop-app-spec.md, section 8).</summary>
public enum CatalogStatus
{
    /// <summary>An install was found on this device.</summary>
    Found,

    /// <summary>No install was found, or one could only be looked for once its location is given.</summary>
    NotFound,

    /// <summary>The app was added on this device before.</summary>
    Added,

    /// <summary>The definition has mistakes, so it cannot be used.</summary>
    Invalid,
}

/// <summary>One app of the catalog, as page 1 lists it.</summary>
public sealed record CatalogApp(string Id, string Name, DefinitionSource Source, bool IsValid);

/// <summary>An app the scan found on this device, with where and when it was last used.</summary>
public sealed record FoundApp(CatalogApp App, string InstallId, string SaveFolder, DateTime? NewestWrite, bool Installed = true);

/// <summary>Page 1's search result: at most <see cref="AddAppFlow.MaxSearchRows"/> apps, and how many matched in all.</summary>
public sealed record SearchResult(IReadOnlyList<CatalogApp> Shown, int Total)
{
    public bool HasMore => Total > Shown.Count;
}

/// <summary>A value only the owner can give, because it cannot be found by looking (section 8, "Fields").</summary>
public enum LocationField
{
    InstallFolder,
    SdCard,
    WinePrefix,
}

/// <summary>Which form page 2 takes, by the core's outcome (section 8's table).</summary>
public enum AddAppPage
{
    /// <summary>A plan: the install, the roles with their sizes, and Add.</summary>
    Plan,

    /// <summary>More than one install was found: the owner picks one.</summary>
    ChooseInstall,

    /// <summary>Not found, or a folder could not be worked out: the fields that would help, and Back.</summary>
    NeedsLocation,

    /// <summary>A conflict or a definition with mistakes: why, and Back.</summary>
    Refused,
}

/// <summary>One role of the chosen install, ticked or not, with what the plan found for it.</summary>
/// <param name="Unticked">Why a role is off by default, for the label beside its checkbox. Null for a default role.</param>
/// <param name="Planned">The plan's measurements, when the role is in the plan.</param>
/// <param name="Notes">The definition's note for the role.</param>
public sealed record RoleChoice(string RoleId, RoleClass Class, bool Ticked, string? Unticked, AppRolePlan? Planned, string? Notes);

/// <summary>Page 2 as the window shows it.</summary>
/// <param name="Found">For a plan: the install used and what showed it is there.</param>
/// <param name="Candidates">The installs to pick from, or those looked for and why each was not found.</param>
/// <param name="Fields">The location fields offered, with their values so far.</param>
/// <param name="Details">The core's own message, under Details; null for a plan.</param>
public sealed record AddAppView(
    AddAppPage Page,
    string AppId,
    string AppName,
    string Title,
    IReadOnlyList<string> Sentences,
    string? Found,
    IReadOnlyList<InstallCandidate> Candidates,
    IReadOnlyList<(LocationField Field, string? Value)> Fields,
    IReadOnlyList<RoleChoice> Roles,
    IReadOnlyList<string> Quirks,
    AppAddPlan? Plan,
    string? Details)
{
    /// <summary>Add is offered for a plan with at least one role ticked.</summary>
    public bool CanAdd => Plan is not null && Roles.Any(r => r.Ticked);
}

/// <summary>What Add did: added, refused before anything was written, or found that the plan changed meanwhile.</summary>
/// <param name="Added">The mappings added, in order, for "Apply it now?".</param>
/// <param name="Changed">The plan made again, when it differs from the one shown: the owner checks it again.</param>
public sealed record AddAppResult(IReadOnlyList<string> Added, string? Refused, AddAppView? Changed)
{
    public bool Succeeded => Added.Count > 0;

    /// <summary>The mappings added whose folder was already a link, as when an app added and applied before is added again.</summary>
    public IReadOnlyList<string> AlreadyLinked { get; init; } = [];

    /// <summary>The mappings added that Apply still has to link.</summary>
    public IReadOnlyList<string> ToApply => Added.Except(AlreadyLinked, StringComparer.Ordinal).ToList();
}

/// <summary>
/// Add app as the window drives it (section 8): page 1 lists the catalog and detects in the background;
/// page 2 is <see cref="Workspace.PlanAppAdd"/>'s outcome, made again whenever a field, the install or a
/// role changes. Planning only reads, so it runs without the operation lock; Add takes the lock, plans
/// once more with the same request, and writes only if that plan is the one the owner saw.
/// </summary>
public sealed class AddAppFlow(LocalStore local, ResolverEnvironment environment)
{
    public const string DifferentNow = "Something changed since this page was made. Check it again, then add.";

    private AppAddRequest? _request;
    private AddAppView? _lastPlan;

    /// <summary>Page 1: the catalog's apps, shared definitions over built-in ones, in name order.</summary>
    public IReadOnlyList<CatalogApp> Catalog()
    {
        var catalog = Open().Definitions;
        return catalog.AvailableIds(includeImported: false)
            .Select(id => (Id: id, Entry: catalog.Find(id)!))
            .Select(e => new CatalogApp(e.Id, e.Entry.Definition?.Name ?? e.Id, e.Entry.Source, e.Entry.IsValid))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The most rows a search shows; the rest are reached by typing more.</summary>
    public const int MaxSearchRows = 50;

    /// <summary>
    /// Page 1's list of apps found on this device, as they are found (so the window can show progress),
    /// each app once, with its newest-written install first. Read-only; slow, so call it off the UI thread.
    /// Pass <see cref="ScanOptions.Deep"/> for the opt-in deep scan; a row then has <see cref="FoundApp.Installed"/> false when no install is detected.
    /// The caller orders what it has by <see cref="FoundApp.NewestWrite"/>, newest first, nulls last.
    /// </summary>
    public IEnumerable<FoundApp> FoundApps(ScanOptions? options = null, CancellationToken cancel = default)
    {
        var workspace = Open();
        var catalog = workspace.Definitions;
        var device = workspace.Shared.TryReadDevice(workspace.DeviceName);
        var definitions = catalog.FindAll(catalog.AvailableIds())
            .Where(e => e.Entry.Definition is not null)
            .ToDictionary(e => e.Id, e => e.Entry, StringComparer.Ordinal);
        DeviceValues ValuesFor(string id) =>
            device?.Apps?.GetValueOrDefault(id) is { } stored ? new DeviceValues(stored.InstallDir, stored.SdCard, stored.WinePrefix) : DeviceValues.None;

        var best = new Dictionary<string, ScanHit>(StringComparer.Ordinal);
        foreach (var hit in InstallScan.Run(definitions.Values.Select(e => e.Definition!), environment, ValuesFor, options, cancel))
        {
            // One row per app: a later hit for another install of the same app replaces it only when newer.
            if (best.TryGetValue(hit.AppId, out var seen) && !hit.IsBetterThan(seen))
            {
                continue;
            }

            best[hit.AppId] = hit;
            var entry = definitions[hit.AppId];
            yield return new FoundApp(new CatalogApp(hit.AppId, entry.Definition!.Name, entry.Source, true), hit.InstallId, hit.SaveFolder, hit.NewestWrite, hit.Installed);
        }
    }

    /// <summary>Orders found apps newest save first; an app with no file in its folder goes last.</summary>
    public static IReadOnlyList<FoundApp> Newest(IEnumerable<FoundApp> found) =>
        found.OrderByDescending(f => f.NewestWrite ?? DateTime.MinValue).ThenBy(f => f.App.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Page 1's search over every definition, imported ones too: by name, ID or Steam app ID. Shows
    /// at most <see cref="MaxSearchRows"/>, built only for those.
    /// </summary>
    public SearchResult Search(string? text)
    {
        var catalog = Open().Definitions;
        var ids = catalog.Search(text);
        var shown = catalog.FindAll(ids.Take(MaxSearchRows))
            .Select(e => new CatalogApp(e.Id, e.Entry.Definition?.Name ?? e.Id, e.Entry.Source, e.Entry.IsValid))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new SearchResult(shown, ids.Count);
    }

    /// <summary>Page 1's search: the apps whose name or ID contains the text, ignoring case.</summary>
    public static IReadOnlyList<CatalogApp> Filter(IReadOnlyList<CatalogApp> apps, string? text)
    {
        ArgumentNullException.ThrowIfNull(apps);
        var typed = text?.Trim() ?? string.Empty;
        return typed.Length == 0
            ? apps
            : apps.Where(a => a.Name.Contains(typed, StringComparison.OrdinalIgnoreCase) || a.Id.Contains(typed, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Page 1's label for one app. Reads only; slow for a Steam library, so the window calls it off the UI thread.</summary>
    public CatalogStatus Detect(string appId)
    {
        var workspace = Open();
        // Added means a folder of the app is set on this device (section 8), not that the device file remembers
        // how the app was found: "Stop using here" drops the folder and keeps that record (found by the owner).
        var device = workspace.Shared.TryReadDevice(workspace.DeviceName);
        if (device?.Paths.Keys.Any(id => id.StartsWith(appId + ".", StringComparison.Ordinal)) == true)
        {
            return CatalogStatus.Added;
        }

        if (workspace.Definitions.Find(appId)?.Definition is not { } definition)
        {
            return CatalogStatus.Invalid;
        }

        var stored = device?.Apps?.GetValueOrDefault(appId);
        var values = new DeviceValues(stored?.InstallDir, stored?.SdCard, stored?.WinePrefix);
        var resolver = new PathResolver(environment);
        return definition.Installs.Where(i => i.Os.Contains(environment.Os)).Any(i => resolver.Detect(i, values).IsResolved)
            ? CatalogStatus.Found
            : CatalogStatus.NotFound;
    }

    public static string Label(CatalogStatus status) => status switch
    {
        CatalogStatus.Found => "Found",
        CatalogStatus.NotFound => "Not found",
        CatalogStatus.Added => "Added",
        CatalogStatus.Invalid => "Has mistakes",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "A status with no label."),
    };

    /// <summary>Page 2 for an app, from page 1 or from a row's "Find the app again" and "Add".</summary>
    public AddAppView Start(string appId)
    {
        _request = new AppAddRequest(appId);
        _lastPlan = null;
        return Plan();
    }

    /// <summary>The install picked when more than one was found.</summary>
    public AddAppView ChooseInstall(string installId) => Plan(Request with { Install = installId, Roles = null });

    /// <summary>A location field changed: the plan is made again with it. Empty clears it.</summary>
    public AddAppView SetLocation(LocationField field, string? path)
    {
        var value = string.IsNullOrWhiteSpace(path) ? null : ExpandHome(path.Trim());
        if (value is not null && !Path.IsPathFullyQualified(value))
        {
            var view = Plan(Request);
            return view with { Sentences = [.. view.Sentences, $"'{path}' is not a full path. Give the whole path, or choose the folder."] };
        }

        return Plan(field switch
        {
            LocationField.InstallFolder => Request with { InstallDir = value },
            LocationField.SdCard => Request with { SdCard = value },
            _ => Request with { WinePrefix = value },
        });
    }

    /// <summary>A role ticked or unticked. With none ticked the page stays, and Add is off.</summary>
    public AddAppView SetRole(string roleId, bool ticked)
    {
        var shown = _lastPlan ?? throw new InvalidOperationException("Roles are chosen on a plan.");
        var keep = shown.Roles.Where(r => r.RoleId == roleId ? ticked : r.Ticked).Select(r => r.RoleId).ToList();
        if (keep.Count == 0)
        {
            return _lastPlan = shown with { Roles = shown.Roles.Select(r => r with { Ticked = false }).ToList() };
        }

        var view = Plan(Request with { Install = shown.Plan!.Install, Roles = keep });
        if (view.Page != AddAppPage.Plan)
        {
            // The role ticked cannot be added as things are: say why, and leave the plan as it was.
            return shown with { Sentences = [.. view.Sentences.Take(1), .. view.Details is { } d ? new[] { d } : []] };
        }

        return view;
    }

    /// <summary>Back from an outcome that is not a plan: the last plan if there was one, else null for page 1.</summary>
    public AddAppView? Back() => _lastPlan;

    /// <summary>
    /// Add (section 8): under the operation lock, the plan is made again with the same request and written
    /// only if it matches the one shown. Nothing is linked; Apply does that.
    /// </summary>
    public AddAppResult Add(AddAppView shown)
    {
        ArgumentNullException.ThrowIfNull(shown);
        if (!shown.CanAdd)
        {
            throw new InvalidOperationException("Add needs a plan with a role ticked.");
        }

        var (session, refused) = OperationSession.Begin(local, "app add", NoProcesses.Instance, flatpak: null);
        if (session is null)
        {
            return new AddAppResult([], refused, null);
        }

        using (session)
        {
            try
            {
                var outcome = session.Workspace.PlanAppAdd(Request, environment);
                if (outcome.Plan is not { } plan || !Same(plan, shown.Plan!))
                {
                    var again = View(outcome);
                    return new AddAppResult([], null, again with { Sentences = [DifferentNow, .. again.Sentences] });
                }

                session.Workspace.AddApp(plan);
                return new AddAppResult(plan.Roles.Select(r => r.MappingId).ToList(), null, null)
                {
                    AlreadyLinked = plan.Roles.Where(r => r.IsLink).Select(r => r.MappingId).ToList(),
                };
            }
            catch (StoreException e)
            {
                return new AddAppResult([], e.Message, null);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new AddAppResult([], e.Message, null);
            }
        }
    }

    /// <summary>"&lt;App&gt; added. Apply it now?"</summary>
    public static string AddedQuestion(string appName) => $"{appName} added. Apply it now?";

    /// <summary>"&lt;App&gt; added", when every folder was already linked and there is nothing to ask.</summary>
    public static string AddedTitle(string appName) => $"{appName} added";

    public static string AddedText(IReadOnlyList<string> mappingIds, IReadOnlyList<string>? alreadyLinked = null)
    {
        ArgumentNullException.ThrowIfNull(mappingIds);
        var linked = alreadyLinked ?? [];
        var toApply = mappingIds.Except(linked, StringComparer.Ordinal).ToList();
        var added = $"Added {string.Join(", ", mappingIds)}.";
        if (linked.Count == 0)
        {
            return $"{added} Nothing is linked yet: Apply links each, asking first where this device already has saves.";
        }

        // A folder that is already a link needs no Apply (the CLI says the same); whether the link is
        // SaveTether's and healthy is what the Apps page shows.
        var already = $"Already linked here: {string.Join(", ", linked)}.";
        return toApply.Count == 0
            ? $"{added} {already} There is nothing to apply; the Apps page shows whether each link is healthy."
            : $"{added} {already} Apply links {string.Join(", ", toApply)}, asking first where this device already has saves.";
    }

    public static string FieldLabel(LocationField field) => field switch
    {
        LocationField.InstallFolder => "Install folder",
        LocationField.SdCard => "SD card",
        LocationField.WinePrefix => "Wine prefix (the folder holding drive_c)",
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "A field with no label."),
    };

    /// <summary>A role's size line: "12 files, 3.4 MiB", or what stands instead.</summary>
    public static string SizeOf(AppRolePlan role)
    {
        ArgumentNullException.ThrowIfNull(role);
        if (role.IsLink)
        {
            return "already a link";
        }

        return !role.Exists ? "not there yet" : $"{role.Files} {(role.Files == 1 ? "file" : "files")}, {ActionTexts.Size(role.Bytes)}";
    }

    public static string ClassName(RoleClass roleClass) => roleClass switch
    {
        RoleClass.Save => "saves",
        RoleClass.System => "system files",
        RoleClass.State => "save states",
        RoleClass.Cache => "cache",
        RoleClass.Mod => "mods",
        RoleClass.Media => "media",
        _ => throw new ArgumentOutOfRangeException(nameof(roleClass), roleClass, "A class with no name."),
    };

    private AppAddRequest Request => _request ?? throw new InvalidOperationException("Start comes first.");

    private AddAppView Plan(AppAddRequest? request = null)
    {
        _request = request ?? Request;
        var view = View(Open().PlanAppAdd(_request, environment));
        if (view.Page == AddAppPage.Plan)
        {
            _lastPlan = view;
        }

        return view;
    }

    /// <summary>Section 8's table: one outcome as page 2 shows it.</summary>
    private AddAppView View(AppAddOutcome outcome)
    {
        var request = Request;
        var workspace = Open();
        var definition = workspace.Definitions.Find(request.AppId)?.Definition;
        var name = definition?.Name ?? request.AppId;
        var details = outcome.Message?.Text;
        var empty = new AddAppView(AddAppPage.Refused, request.AppId, name, string.Empty, [], null, outcome.Candidates, [], [], [], null, details);

        if (outcome.Plan is { } plan)
        {
            var install = definition!.Installs.First(i => i.Id == plan.Install);
            var roles = definition.Roles
                .Where(r => r.Paths.ContainsKey(install.Id))
                .Select(r =>
                {
                    var planned = plan.Roles.FirstOrDefault(p => p.RoleId == r.Id);
                    return new RoleChoice(r.Id, r.Class, planned is not null, r.Default ? null : WhyOff(r.Class), planned, r.Notes);
                })
                .ToList();
            var found = outcome.Candidates.FirstOrDefault(c => c.Id == plan.Install)?.Found;
            return empty with
            {
                Page = AddAppPage.Plan,
                Title = $"Add {name}",
                Found = found is null ? $"Install: {plan.Install}." : $"Install: {plan.Install}, found at {found}.",
                Roles = roles,
                Quirks = plan.Quirks,
                Plan = plan,
                Sentences = plan.Roles.Where(r => r.Note is not null).Select(r => $"{r.MappingId}: {r.Note!.Text}").ToList(),
                Details = null,
            };
        }

        var next = outcome.Message?.Next.Select(n => n.Kind).ToHashSet() ?? [];
        switch (outcome.Problem)
        {
            case AppAddProblem.InstallChoiceNeeded:
                return empty with
                {
                    Page = AddAppPage.ChooseInstall,
                    Title = $"More than one install of {name} was found.",
                    Sentences = ["Choose the one whose saves this device should share."],
                    Candidates = outcome.Candidates.Where(c => c.IsDetected).ToList(),
                };

            case AppAddProblem.NotInstalled:
            {
                // The core's reason for an install that needs a value names its token ("{install_dir} needs..."): the
                // window says it in words, since the field for it is right below (found on the Deck). Details keep the core's.
                var sentences = outcome.Candidates
                    .Select(c => c.Problem == ResolveProblem.NeedsValue
                        ? $"{c.Id}: can only be looked for once you give its location below."
                        : $"{c.Id}: {c.Reason?.Text ?? "not found."}")
                    .ToList();
                var installs = definition?.Installs.Where(i => i.Os.Contains(environment.Os)).ToList() ?? [];
                var wanting = next.Contains(NextStepKind.GiveNewLocation)
                    ? installs
                    : installs.Where(i => outcome.Candidates.Any(c => c.Id == i.Id && c.Problem == ResolveProblem.NeedsValue)).ToList();
                if (next.Contains(NextStepKind.GiveNewLocation))
                {
                    sentences.Add($"{name} was not where it was last time, so it may have moved. Give its new location.");
                }

                return empty with
                {
                    Page = AddAppPage.NeedsLocation,
                    Title = $"{name} was not found on this device.",
                    Sentences = sentences,
                    Fields = FieldsFor(wanting, definition, request),
                };
            }

            case AppAddProblem.Unresolved:
            {
                var installs = definition?.Installs
                    .Where(i => i.Id == request.Install || (request.Install is null && outcome.Candidates.Any(c => c.Id == i.Id && c.IsDetected)))
                    .ToList() ?? [];
                var helps = next.Contains(NextStepKind.GiveLocation) || next.Contains(NextStepKind.ChooseLocation);
                return empty with
                {
                    Page = AddAppPage.NeedsLocation,
                    Title = "A folder could not be worked out",
                    Sentences = [outcome.Message?.Text ?? "A folder could not be worked out."],
                    Fields = helps ? FieldsFor(installs, definition, request) : [],
                };
            }

            case AppAddProblem.InvalidDefinition:
                return empty with { Title = $"The definition for {name} has mistakes, so it cannot be used.", Sentences = [details ?? string.Empty] };

            case AppAddProblem.Conflict:
                return empty with { Title = "Already set up differently", Sentences = [Conflict(outcome.Conflict, details)] };

            case AppAddProblem.NotLocal:
                return empty with
                {
                    Title = "A folder is on a network drive",
                    Sentences = [$"On Windows SaveTether can only link a folder on a local drive, so {name} cannot be added from where it is found."],
                };

            default:
                // unknown-app, unknown-install, unknown-role: the window offers only what the definition lists (section 10).
                return empty with { Title = "Something went wrong", Sentences = [details ?? "The app could not be planned."] };
        }
    }

    /// <summary>A conflict's facts in the app's words (section 8: another target, applied elsewhere, a folder in use).</summary>
    public static string Conflict(ConflictFacts? facts, string? message) => facts switch
    {
        { Kind: ConflictKind.TargetDiffers } f =>
            $"{f.MappingId} already exists with another shared folder, {f.Target}. The definition names a different one.",
        { Kind: ConflictKind.AppliedElsewhere } f =>
            $"{f.MappingId} is linked at {f.Folder}. Revert it there first, then add the app again.",
        { Kind: ConflictKind.FolderInUse } f =>
            $"The folder is already used by {f.MappingId}, at {f.Folder}. One folder can be shared by one mapping only.",
        { Kind: ConflictKind.TargetInUse } f =>
            $"{f.MappingId} already uses the shared folder {f.Target}.",
        _ => message ?? "It is already set up differently.",
    };

    /// <summary>Shown whether ticked or not, so it says "by default": a ticked role read "Off: ..." on the Deck.</summary>
    private static string WhyOff(RoleClass roleClass) => roleClass switch
    {
        RoleClass.Cache => "Off by default: a cache, which the app rebuilds by itself.",
        RoleClass.Media => "Off by default: screenshots and recordings, which can be large.",
        _ => "Off by default.",
    };

    private List<(LocationField Field, string? Value)> FieldsFor(IEnumerable<InstallDefinition> installs, AppDefinition? definition, AppAddRequest request)
    {
        var workspace = Open();
        var stored = workspace.Shared.TryReadDevice(workspace.DeviceName)?.Apps?.GetValueOrDefault(request.AppId);
        var fields = new SortedSet<LocationField>();
        foreach (var install in installs)
        {
            string?[] texts =
            [
                install.Detect.Dir, install.Detect.File, install.Base.Path, install.Base.WinPath, install.Base.Prefix?.Path,
                .. definition?.Roles.Select(r => r.Paths.GetValueOrDefault(install.Id)) ?? [],
            ];
            if (texts.Any(t => t?.Contains("{install_dir}", StringComparison.Ordinal) == true))
            {
                fields.Add(LocationField.InstallFolder);
            }

            if (texts.Any(t => t?.Contains("{sd_card}", StringComparison.Ordinal) == true))
            {
                fields.Add(LocationField.SdCard);
            }

            if (install.Base.WinPath is not null && environment.Os == TargetOs.Linux)
            {
                fields.Add(LocationField.WinePrefix);
            }
        }

        return fields.Select(f => (f, f switch
        {
            LocationField.InstallFolder => request.InstallDir ?? stored?.InstallDir,
            LocationField.SdCard => request.SdCard ?? stored?.SdCard,
            _ => request.WinePrefix ?? stored?.WinePrefix,
        })).ToList();
    }

    /// <summary>Whether the plan made under the lock writes what the owner saw.</summary>
    private static bool Same(AppAddPlan now, AppAddPlan shown) =>
        now.Install == shown.Install
        && now.Values == shown.Values
        && now.Roles.Count == shown.Roles.Count
        && now.Roles.Zip(shown.Roles).All(p =>
            p.First.MappingId == p.Second.MappingId
            && p.First.Target == p.Second.Target
            && p.First.Source == p.Second.Source
            && p.First.CreatesMapping == p.Second.CreatesMapping
            && p.First.EntryDepth == p.Second.EntryDepth);

    private Workspace Open() => Workspace.Open(local, NoProcesses.Instance);

    private static string ExpandHome(string path) =>
        !OperatingSystem.IsWindows() && (path == "~" || path.StartsWith("~/", StringComparison.Ordinal))
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..]
            : path;

    /// <summary>Adding an app runs no plan that checks processes.</summary>
    private sealed class NoProcesses : IProcessLister
    {
        public static readonly NoProcesses Instance = new();

        public IReadOnlyList<string> RunningProcessNames() => [];
    }
}
