using SaveTether.Core.Definitions;
using SaveTether.Core.Detection;
using SaveTether.Core.FileSystem;
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
/// <param name="MoreToAdd">For an added app: it has a folder (role) for this device that is not added yet.</param>
public sealed record CatalogApp(string Id, string Name, DefinitionSource Source, bool IsValid, bool MoreToAdd = false);

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
/// <param name="AlreadyAdded">This device already has a folder set for the role: it shows ticked and cannot be changed here (Remove on the Apps page undoes it).</param>
public sealed record RoleChoice(string RoleId, RoleClass Class, bool Ticked, string? Unticked, AppRolePlan? Planned, string? Notes, bool AlreadyAdded = false)
{
    /// <summary>
    /// Where the role's folder is and its size ("G:\...\addons: 3 files, 1 KB"), when known: a ticked role's from the plan, an
    /// unticked one's once measured. Null while it is still to be measured (<see cref="ToMeasure"/>) or cannot be worked out.
    /// </summary>
    public string? Folder { get; init; }

    /// <summary>An unticked role's folder whose size is not read yet: the window reads it in the background (<see cref="AddAppFlow.Sizes"/>).</summary>
    public string? ToMeasure { get; init; }
}

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
    /// <summary>True for an imported definition: converted from the Ludusavi manifest, not tried by hand.</summary>
    public bool Unverified { get; init; }

    /// <summary>For the choose-an-install page: what to show beside an install, by its id (where it was found, or what the owner would enter).</summary>
    public IReadOnlyDictionary<string, string> InstallPaths { get; init; } = new Dictionary<string, string>();

    /// <summary>Under the Install folder field: which folder to choose, from what the definition looks for inside it. Null when there is no field.</summary>
    public string? InstallFolderHint { get; init; }

    /// <summary>For a plan: a note that some store's copy of the app keeps saves in the cloud too; null when none does.</summary>
    public string? CloudNote { get; init; }

    /// <summary>
    /// For a plan: a ticked save folder whose parent folder does not exist, so Apply would refuse it ("The folder that should
    /// contain ... does not exist"). Null when every ticked folder can be linked. A folder that is missing but has a parent is
    /// fine: Apply creates it from the shared copy.
    /// </summary>
    public string? MissingFolderWarning { get; init; }

    /// <summary>Add is offered for a plan with at least one role ticked that is not added yet.</summary>
    public bool CanAdd => Plan is not null && Roles.Any(r => r.Ticked && !r.AlreadyAdded);
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

    // The pages shown since Start, each with the request it was made from, for Back. A page of the same kind as the one shown
    // replaces it (a plan made again when a box is ticked); another kind is a step forward.
    private readonly List<(AddAppView View, AppAddRequest Request)> _pages = [];

    /// <summary>
    /// The sizes of folders not in the plan, which the window reads in the background; kept until the next app is started, so
    /// ticking a box, which makes the page again, does not read them again.
    /// </summary>
    public FolderSizes Sizes { get; private set; } = new();

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

    /// <summary>Page 1's "already added" rows: apps with a folder set on this device, in name order. Reads only.</summary>
    public IReadOnlyList<CatalogApp> AddedApps()
    {
        var workspace = Open();
        var device = workspace.Shared.TryReadDevice(workspace.DeviceName);
        if (device is null)
        {
            return [];
        }

        // A mapping ID is "<app>.<role>"; a hand-added folder has no definition and is not listed here.
        var ids = device.Paths.Keys
            .Select(id => id.Split('.', 2)[0])
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return workspace.Definitions.FindAll(ids.Where(id => workspace.Definitions.Find(id) is not null).ToList())
            .Select(e => new CatalogApp(e.Id, e.Entry.Definition?.Name ?? e.Id, e.Entry.Source, e.Entry.IsValid, MoreToAdd: HasRoleLeft(e.Entry.Definition, e.Id, device.Paths.Keys)))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Whether the app has a folder for this OS that this device has not added: a role with a path for an install on
    /// this OS and no folder set. An added app with none left has nothing more to choose, so page 1 does not open it.
    /// </summary>
    private bool HasRoleLeft(AppDefinition? definition, string appId, IEnumerable<string> addedMappingIds)
    {
        if (definition is null)
        {
            return false;
        }

        var added = addedMappingIds.ToHashSet(StringComparer.Ordinal);
        var installs = definition.Installs.Where(i => i.Os.Contains(environment.Os)).Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        return definition.Roles.Any(r => r.Paths.Keys.Any(installs.Contains) && !added.Contains(r.MappingIdFor(appId)));
    }

    /// <summary>A found row's "last played": the newest save's date (local), or why there is none.</summary>
    public static string LastPlayed(DateTime? newestWrite) =>
        newestWrite is { } when
            ? "Last played " + when.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
            : "No save files yet";

    /// <summary>The most rows a search shows; the rest are reached by typing more.</summary>
    public const int MaxSearchRows = 50;

    /// <summary>
    /// Page 1's list of apps found on this device, as they are found (so the window can show progress),
    /// each app once, with its newest-written install first. Read-only; slow, so call it off the UI thread.
    /// Pass <see cref="ScanOptions.Deep"/> for the opt-in deep scan; a row then has <see cref="FoundApp.Installed"/> false when no install is detected.
    /// The caller orders what it has by <see cref="FoundApp.NewestWrite"/>, newest first, nulls last.
    /// </summary>
    public IEnumerable<FoundApp> FoundApps(CancellationToken cancel = default) => FoundApps(ScanOptions.Default, cancel);

    /// <summary>As <see cref="FoundApps(CancellationToken)"/>, with the scan's options.</summary>
    public IEnumerable<FoundApp> FoundApps(ScanOptions options, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (var (hit, entry) in Open().ScanFound(environment, options, cancel))
        {
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
        return definition.Installs.Where(i => i.Os.Contains(environment.Os)).Any(i => InstallScan.IsInstalled(definition, i, resolver, values))
            ? CatalogStatus.Found
            : CatalogStatus.NotFound;
    }

    /// <summary>Page 1's column names while it lists what was found and added: name, last played, and the marks or "Added".</summary>
    public static readonly IReadOnlyList<string> FoundColumns = ["App", "Last played", "Notes"];

    /// <summary>Page 1's column names while it shows search results: name, marks, and <see cref="Label(CatalogStatus)"/>.</summary>
    public static readonly IReadOnlyList<string> SearchColumns = ["App", "Notes", "On this device"];

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
        _pages.Clear();
        Sizes = new();
        var view = Plan();
        if (view.Page is not (AddAppPage.Plan or AddAppPage.ChooseInstall or AddAppPage.NeedsLocation or AddAppPage.Refused))
        {
            return Shown(view);
        }

        // The installs this system lists for the app: the found ones and, for a definition written by hand, the one that
        // needs a folder from the owner (a portable copy can be anywhere). With more than one the owner picks first; a
        // lone portable install goes to its folder page, so the folder it was last given can be changed.
        var workspace = Open();
        var entry = workspace.Definitions.Find(appId);
        if (entry?.Definition is not { } definition || entry.Source == DefinitionSource.Imported)
        {
            return Shown(view);
        }

        // Only installs the core looked at: a refusal for the auto-picked install (a folder another mapping uses) still lists
        // them all, so the chooser is built from the candidates before that install's refusal is shown.
        var options = definition.Installs
            .Where(i => i.Os.Contains(environment.Os))
            .Where(i => view.Candidates.Any(c => c.Id == i.Id && (c.IsDetected || UsesInstallFolder(definition, i))))
            .ToList();
        if (options.Count > 1)
        {
            return Shown(view with
            {
                Page = AddAppPage.ChooseInstall,
                Title = SelectInstall(view.AppName),
                Sentences = [],
                Found = null,
                Candidates = options.Select(i => view.Candidates.First(c => c.Id == i.Id)).ToList(),
                Fields = [],
                Roles = [],
                Quirks = [],
                Plan = null,
                InstallFolderHint = null,
                InstallPaths = options.ToDictionary(
                    i => i.Id,
                    // An install that needs a folder always shows what the owner would enter, even when the folder given before
                    // still holds it: the folder page shows that one.
                    i => UsesInstallFolder(definition, i) ? ManualPathText(definition, i) : view.Candidates.First(c => c.Id == i.Id).Found ?? string.Empty),
            });
        }

        if (options.Count == 1 && view.Page != AddAppPage.NeedsLocation && UsesInstallFolder(definition, options[0]))
        {
            return ChooseInstall(options[0].Id);
        }

        return Shown(view);
    }

    /// <summary>Where a manual install would hold the saves, as the owner would enter it: "&lt;install folder&gt;/mlc01/usr/save".</summary>
    private static string ManualPathText(AppDefinition definition, InstallDefinition install)
    {
        const string token = "{install_dir}";
        var baseRelative = install.Base.Path is { } b && b.StartsWith(token, StringComparison.Ordinal) ? b[token.Length..].Trim('/') : string.Empty;
        var shared = definition.Roles
            .Where(r => r.Default && r.Paths.ContainsKey(install.Id))
            .Select(r => string.Join('/', new[] { baseRelative, r.Paths[install.Id] }.Where(part => part.Length > 0)))
            .ToList();
        return shared.Count == 0 ? "<install folder>" : string.Join(", ", shared.Select(p => $"<install folder>/{p}"));
    }

    private static bool UsesInstallFolder(AppDefinition definition, InstallDefinition install)
    {
        string?[] texts =
        [
            install.Detect.Dir, install.Detect.File, install.Base.Path, install.Base.WinPath, install.Base.Prefix?.Path,
            .. definition.Roles.Select(r => r.Paths.GetValueOrDefault(install.Id)),
        ];
        return texts.Any(t => t?.Contains("{install_dir}", StringComparison.Ordinal) == true);
    }

    /// <summary>The install picked when more than one was found.</summary>
    public AddAppView ChooseInstall(string installId)
    {
        var request = Request with { Install = installId, Roles = null };
        var workspace = Open();
        var entry = workspace.Definitions.Find(request.AppId);
        var definition = entry?.Definition;
        if (entry?.Source != DefinitionSource.Imported
            && definition?.Installs.FirstOrDefault(i => i.Id == installId) is { } install && UsesInstallFolder(definition, install))
        {
            // A portable install: ask for its folder, with the one given or stored before filled in.
            _request = request;
            var stored = workspace.Shared.TryReadDevice(workspace.DeviceName)?.Apps?.GetValueOrDefault(request.AppId);
            return Shown(new AddAppView(AddAppPage.NeedsLocation, request.AppId, definition.Name, SelectInstall(definition.Name), [], null, [], [], [], [], null, null)
            {
                Fields = [(LocationField.InstallFolder, request.InstallDir ?? stored?.InstallDir)],
                InstallFolderHint = InstallFolderHintFor(definition, [install]),
            });
        }

        return Shown(Plan(request));
    }

    /// <summary>A location field changed: the plan is made again with it. Empty clears it.</summary>
    public AddAppView SetLocation(LocationField field, string? path)
    {
        // Back to this page shows the folder as given, not as the page first had it.
        if (_pages.Count > 0 && _pages[^1].View.Page == AddAppPage.NeedsLocation)
        {
            var (page, made) = _pages[^1];
            _pages[^1] = (page with { Fields = page.Fields.Select(f => f.Field == field ? (f.Field, path) : f).ToList() }, made);
        }

        var value = string.IsNullOrWhiteSpace(path) ? null : ExpandHome(path.Trim());
        if (value is not null && !Path.IsPathFullyQualified(value))
        {
            var view = Plan(Request);
            return Shown(view with { Sentences = [.. view.Sentences, $"'{path}' is not a full path. Give the whole path, or choose the folder."] });
        }

        return Shown(Plan(field switch
        {
            LocationField.InstallFolder => Request with { InstallDir = value },
            LocationField.SdCard => Request with { SdCard = value },
            _ => Request with { WinePrefix = value },
        }));
    }

    /// <summary>A role ticked or unticked. With none ticked the page stays, and Add is off.</summary>
    public AddAppView SetRole(string roleId, bool ticked)
    {
        var shown = _lastPlan ?? throw new InvalidOperationException("Roles are chosen on a plan.");
        if (shown.Roles.Any(r => r.RoleId == roleId && r.AlreadyAdded))
        {
            // Added before: Add never removes a folder, so the box does not change here.
            return shown;
        }

        var keep = shown.Roles.Where(r => r.RoleId == roleId ? ticked : r.Ticked).Select(r => r.RoleId).ToList();
        if (keep.Count == 0)
        {
            return Shown(shown with { Roles = shown.Roles.Select(r => r with { Ticked = false }).ToList() });
        }

        var request = _request;
        var view = Plan(Request with { Install = shown.Plan!.Install, Roles = keep });
        if (view.Page != AddAppPage.Plan)
        {
            // The role ticked cannot be added as things are: say why, and leave the plan, and the request it was made from, as they were.
            _request = request;
            return Shown(shown with { Sentences = [.. view.Sentences.Take(1), .. view.Details is { } d ? new[] { d } : []] });
        }

        return Shown(view);
    }

    /// <summary>
    /// Back: the page shown before this one since <see cref="Start"/>, as it was left (a folder given stays filled in), or null
    /// for page 1. Found by the owner: Back from a folder page went to a plan never shown, and from a plan skipped the folder page.
    /// </summary>
    public AddAppView? Back()
    {
        if (_pages.Count > 0)
        {
            _pages.RemoveAt(_pages.Count - 1);
        }

        if (_pages.Count == 0)
        {
            _lastPlan = null;
            return null;
        }

        var (view, request) = _pages[^1];
        _request = request;
        _lastPlan = view.Page == AddAppPage.Plan ? view : null;
        return view;
    }

    /// <summary>A page about to be shown: kept for Back, and the plan ticking works on when it is one.</summary>
    private AddAppView Shown(AddAppView view)
    {
        if (_pages.Count > 0 && _pages[^1].View.Page == view.Page)
        {
            _pages[^1] = (view, Request);
        }
        else
        {
            _pages.Add((view, Request));
        }

        _lastPlan = view.Page == AddAppPage.Plan ? view : null;
        return view;
    }

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

                // The folders locked as "added" were told from this device's paths when the page was made. If one was removed
                // (or moved) since, the plan can still match, and Add would write it back unseen: show the page again instead.
                if (outcome.Plan is { } checkedPlan && Same(checkedPlan, shown.Plan!) && !SameAdded(View(outcome).Roles, shown.Roles))
                {
                    var changedView = View(outcome);
                    return new AddAppResult([], null, Shown(changedView with { Sentences = [DifferentNow, .. changedView.Sentences] }));
                }

                if (outcome.Plan is not { } plan || !Same(plan, shown.Plan!))
                {
                    var again = View(outcome);
                    return new AddAppResult([], null, Shown(again with { Sentences = [DifferentNow, .. again.Sentences] }));
                }

                session.Workspace.AddApp(plan);

                // Roles added before are written again unchanged; only the new ones are reported, for "Apply it now?".
                var before = shown.Roles.Where(r => r.AlreadyAdded).Select(r => r.RoleId).ToHashSet(StringComparer.Ordinal);
                var added = plan.Roles.Where(r => !before.Contains(r.RoleId)).ToList();
                return new AddAppResult(added.Select(r => r.MappingId).ToList(), null, null)
                {
                    AlreadyLinked = added.Where(r => r.IsLink).Select(r => r.MappingId).ToList(),
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
            return $"{added} Nothing is linked yet: Apply links each, asking first where this device already has data.";
        }

        // A folder that is already a link needs no Apply (the CLI says the same); whether the link is
        // SaveTether's and healthy is what the Apps page shows.
        var already = $"Already linked here: {string.Join(", ", linked)}.";
        return toApply.Count == 0
            ? $"{added} {already} There is nothing to apply; the Apps page shows whether each link is healthy."
            : $"{added} {already} Apply links {string.Join(", ", toApply)}, asking first where this device already has data.";
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
        return SizeOf(role.Exists, role.Bytes, role.Files);
    }

    // A linked folder shows its size too, the shared copy's: "Added already." says it is linked (asked for by the owner).
    public static string SizeOf(bool exists, long bytes, int files) =>
        !exists ? "not there yet" : $"{files} {(files == 1 ? "file" : "files")}, {ActionTexts.Size(bytes)}";

    /// <summary>An unticked role's line: its folder and, once read, its size ("path: 3 files, 1 KB").</summary>
    public static string FolderLine(string source, string size) => $"{source}: {size}";

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
        return View(Open().PlanAppAdd(_request, environment));
    }

    /// <summary>
    /// A role that is not in the plan: where its folder would be, with its size when already known; else the window reads it later,
    /// in the background. A link is read too: its size is the shared copy's, which is what the owner wants to see.
    /// </summary>
    private RoleChoice WithFolder(RoleChoice choice, InstallDefinition install, RoleDefinition role, AppAddPlan plan)
    {
        var values = new DeviceValues(plan.Values.InstallDir, plan.Values.SdCard, plan.Values.WinePrefix);
        var resolved = new PathResolver(environment).ResolveRole(install, role, values);
        if (!resolved.IsResolved || resolved.Path is not { } source)
        {
            return choice;
        }

        return Sizes.Known(source) is { } known ? choice with { Folder = FolderLine(source, known) } : choice with { ToMeasure = source };
    }

    /// <summary>
    /// Whether an added role's folder is somewhere else now than this device has it. The place is worked out for the role
    /// whether or not it is in the plan (a default-off role is not), so a moved optional folder can still be added again.
    /// When the place cannot be worked out the role stays locked: there is nothing to record.
    /// </summary>
    private bool HasMoved(InstallDefinition install, RoleDefinition role, AppAddPlan plan, AppRolePlan? planned, DevicePath had)
    {
        string? now = planned?.Source;
        if (now is null)
        {
            var values = new DeviceValues(plan.Values.InstallDir, plan.Values.SdCard, plan.Values.WinePrefix);
            var resolved = new PathResolver(environment).ResolveRole(install, role, values);
            now = resolved.IsResolved ? resolved.Path : null;
        }

        return now is not null && !string.Equals(PathRules.Normalize(now), PathRules.Normalize(had.Source), StringComparison.Ordinal);
    }

    /// <summary>
    /// The heading of the install pages (choosing one, giving its folder); the plan's is "<app> Add Folders" and the hand
    /// form's "<app> Add Custom Folder" (asked for by the owner, one pattern across the flow).
    /// </summary>
    public static string SelectInstall(string appName) => $"{appName} Select Install";

    /// <summary>The location page's first line when no install was found.</summary>
    public const string NoInstall = "No installations found on this device.";

    /// <summary>Page 2's unverified note, for an imported definition.</summary>
    public const string UnverifiedNote =
        "Unverified: converted from the Ludusavi manifest, not tried by hand. Check the folders before adding.";

    /// <summary>Page 2's cloud-saves note for the stores whose copies keep saves in the cloud, or null when none does.</summary>
    public static string? CloudNoteFor(AppDefinition? definition)
    {
        if (definition is null || definition.CloudSaves.Count == 0)
        {
            return null;
        }

        var stores = string.Join(", ", definition.CloudSaves.Select(StoreName));
        return $"The {stores} copy also syncs saves to its own cloud, which can overwrite SaveTether's changes. Check its cloud sync before sharing.";
    }

    private static string StoreName(string store) => store switch
    {
        "steam" => "Steam",
        "gog" => "GOG",
        "epic" => "Epic",
        "xbox" => "Xbox",
        "ubisoft" => "Ubisoft",
        "ea" => "EA",
        _ => store,
    };

    /// <summary>Section 8's table: one outcome as page 2 shows it.</summary>
    private AddAppView View(AppAddOutcome outcome)
    {
        var request = Request;
        var workspace = Open();
        var entry = workspace.Definitions.Find(request.AppId);
        var definition = entry?.Definition;
        var name = definition?.Name ?? request.AppId;
        var details = outcome.Message?.Text;
        var empty = new AddAppView(AddAppPage.Refused, request.AppId, name, string.Empty, [], null, outcome.Candidates, [], [], [], null, details);

        if (outcome.Plan is { } plan)
        {
            var install = definition!.Installs.First(i => i.Id == plan.Install);
            var device = workspace.Shared.TryReadDevice(workspace.DeviceName);
            var addedHere = device?.Paths ?? new Dictionary<string, DevicePath>();
            var roles = definition.Roles
                .Where(r => r.Paths.ContainsKey(install.Id))
                .Select(r =>
                {
                    var planned = plan.Roles.FirstOrDefault(p => p.RoleId == r.Id);
                    // Added already: this device has the folder set, and the plan would set the same one. A folder that moved (the
                    // plan finds the role somewhere else now) is not "already": adding it again records where it is now.
                    var mappingId = r.MappingIdFor(request.AppId);
                    var already = addedHere.TryGetValue(mappingId, out var had)
                        && !HasMoved(install, r, plan, planned, had);
                    var choice = new RoleChoice(r.Id, r.Class, planned is not null || already, r.Default || already ? null : WhyOff(r.Class), planned, r.Notes, already);
                    return planned is not null ? choice with { Folder = $"{planned.Source}: {SizeOf(planned)}" } : WithFolder(choice, install, r, plan);
                })
                .ToList();
            var found = outcome.Candidates.FirstOrDefault(c => c.Id == plan.Install)?.Found;

            // Apply refuses a folder whose parent is missing, whatever the shared copy holds: say so before Add, not at Apply.
            var noParent = roles
                .Where(r => r.Ticked && !r.AlreadyAdded && r.Planned is { Exists: false, IsLink: false })
                .Select(r => (r.RoleId, Parent: Path.GetDirectoryName(PathRules.Normalize(r.Planned!.Source))))
                .Where(x => x.Parent is not null && SafeFs.Probe(PathRules.ResolveAll(x.Parent)) != EntryKind.Directory)
                .ToList();
            var warning = noParent.Count == 0
                ? null
                : $"{string.Join(" ", noParent.Select(x => $"The folder {x.Parent} does not exist, so Apply will refuse to link {x.RoleId}."))} Check the folder you chose, and that the app is installed.";
            return empty with
            {
                MissingFolderWarning = warning,
                Page = AddAppPage.Plan,
                Title = $"{name} Add Folders",
                Found = found is null ? $"Install: {plan.Install}." : $"Install: {plan.Install}, found at {found}.",
                Roles = roles,
                Quirks = plan.Quirks,
                Plan = plan,
                Unverified = entry?.Source == DefinitionSource.Imported,
                CloudNote = CloudNoteFor(definition),
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
                    Title = SelectInstall(name),
                    Sentences = [],
                    Candidates = outcome.Candidates.Where(c => c.IsDetected).ToList(),
                };

            case AppAddProblem.NotInstalled:
            {
                // An install that needs a value is not named: the field for it is right below. Details keep the core's reasons.
                // The title is the app's name; the first line, in amber, says nothing was found (asked for by the owner).
                var sentences = new List<string> { NoInstall };
                sentences.AddRange(outcome.Candidates
                    .Where(c => c.Problem != ResolveProblem.NeedsValue)
                    .Select(c => $"{c.Id}: {c.Reason?.Text ?? "not found."}"));
                var installs = definition?.Installs.Where(i => i.Os.Contains(environment.Os)).ToList() ?? [];
                var wanting = next.Contains(NextStepKind.GiveNewLocation)
                    ? installs
                    : installs.Where(i => outcome.Candidates.Any(c => c.Id == i.Id && c.Problem == ResolveProblem.NeedsValue)).ToList();
                if (next.Contains(NextStepKind.GiveNewLocation))
                {
                    sentences.Add($"{name} was not where it was last time. Give its new location.");
                }

                return empty with
                {
                    Page = AddAppPage.NeedsLocation,
                    Title = SelectInstall(name),
                    Sentences = sentences,
                    Fields = FieldsFor(wanting, definition, request),
                    InstallFolderHint = InstallFolderHintFor(definition, wanting),
                    Details = null,
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
                    Title = SelectInstall(name),
                    Sentences = [outcome.Message?.Text ?? "A folder could not be worked out."],
                    Fields = helps ? FieldsFor(installs, definition, request) : [],
                    InstallFolderHint = helps ? InstallFolderHintFor(definition, installs) : null,
                    Details = null,
                };
            }

            case AppAddProblem.InvalidDefinition:
                return empty with { Title = $"The definition for {name} has mistakes, so it cannot be used.", Sentences = [details ?? string.Empty], Details = null };

            case AppAddProblem.Conflict:
                return empty with { Title = "Already Set Up Differently", Sentences = [Conflict(outcome.Conflict, details)] };

            case AppAddProblem.NotLocal:
                return empty with
                {
                    Title = "A Folder Is on a Network Drive",
                    Sentences = [$"On Windows SaveTether can only link a folder on a local drive, so {name} cannot be added from where it is found."],
                };

            default:
                // unknown-app, unknown-install, unknown-role: the window offers only what the definition lists (section 10).
                return empty with { Title = "Something Went Wrong", Sentences = [details ?? "The app could not be planned."], Details = null };
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

    /// <summary>
    /// Says which folder to choose for {install_dir}: the one that contains the folders the definition shares, as the
    /// definition writes them for this system ("mlc01/usr/save"). Worked out from the definition, so every app with an
    /// install folder gets it. An install with no default folder to share falls back to what it looks for.
    /// </summary>
    public static string? InstallFolderHintFor(AppDefinition? definition, IEnumerable<InstallDefinition> installs)
    {
        const string token = "{install_dir}";
        var hints = new List<string>();
        foreach (var install in installs)
        {
            var detect = install.Detect.Dir ?? install.Detect.File;
            if (detect is null || !detect.StartsWith(token, StringComparison.Ordinal))
            {
                continue;
            }

            var marker = detect[token.Length..].Trim('/');
            var baseRelative = install.Base.Path is { } b && b.StartsWith(token, StringComparison.Ordinal) ? b[token.Length..].Trim('/') : string.Empty;
            var shared = (definition?.Roles ?? [])
                .Where(r => r.Default && r.Paths.ContainsKey(install.Id))
                .Select(r => string.Join('/', new[] { baseRelative, r.Paths[install.Id] }.Where(part => part.Length > 0)))
                .ToList();
            var contains = shared.Count > 0 ? shared : marker.Length > 0 ? [marker] : [];
            hints.Add(contains.Count == 0
                ? "Select the folder the app is installed in."
                : $"Select the folder containing {string.Join(" or ", contains.Select(p => $"\"{p}\""))}.");
        }

        return hints.Count == 0 ? null : string.Join(" ", hints.Distinct());
    }

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
    private static bool SameAdded(IReadOnlyList<RoleChoice> now, IReadOnlyList<RoleChoice> shown) =>
        now.Count == shown.Count && now.Zip(shown).All(p => p.First.RoleId == p.Second.RoleId && p.First.AlreadyAdded == p.Second.AlreadyAdded);

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
}
