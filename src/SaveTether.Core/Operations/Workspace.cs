using SaveTether.Core.Backup;
using SaveTether.Core.Execution;
using SaveTether.Core.FileSystem;
using SaveTether.Core.Health;
using SaveTether.Core.Model;
using SaveTether.Core.Planning;
using SaveTether.Core.Storage;

namespace SaveTether.Core.Operations;

/// <summary>What "init" was asked to do.</summary>
/// <param name="BackupRoot">Where this device keeps backups, when the owner said; null to use the default.</param>
/// <param name="DefaultBackupRoot">Where backups go when the owner did not say.</param>
public sealed record InitRequest(
    string SyncRoot, string DeviceName, bool CreateRoot, bool TakeOver, string? BackupRoot, string DefaultBackupRoot);

/// <param name="Devices">Every device that has a file in the sync root now, including this one.</param>
public sealed record InitResult(
    string SyncRoot, string DeviceName, string BackupRoot, bool CreatedRoot, bool NewDevice, bool TookOver,
    IReadOnlyList<string> Devices, IReadOnlyList<Note> Notes);

/// <param name="CreatedMapping">False when the shared mapping already existed and only this device's path was set.</param>
/// <param name="EntryDepthChanged">True when an existing mapping's entry depth was changed by this call.</param>
public sealed record MapResult(MappingFile Mapping, DevicePath Path, bool CreatedMapping, bool EntryDepthChanged);

/// <param name="SyncRootProblem">
/// Null when the sync root can be read. Otherwise why not; <see cref="Mappings"/> then holds only
/// the mappings this device's own record says it applied, since the shared list is out of reach.
/// </param>
public sealed record StatusReport(
    string DeviceName, string SyncRoot, bool DeviceFileMismatch, bool JournalOpen, IReadOnlyList<MappingHealth> Mappings,
    string? SyncRootProblem = null)
{
    public bool SyncRootAvailable => SyncRootProblem is null;

    /// <summary>
    /// The program the autostart entry starts, when the entry is there and the program is not (desktop-app-spec.md,
    /// section 11). Set by the front end that reads the entry, not by <see cref="Workspace.Status"/>.
    /// </summary>
    public string? AutostartMissing { get; init; }
}

/// <summary>
/// This device, set up: its local files, the shared files, and the operations the commands
/// run on them. The command line only parses arguments, calls this, and prints the result.
/// A refusal that is not a planner refusal is thrown as a <see cref="StoreException"/> or one of
/// the other I/O exceptions, with a message meant for the owner; nothing has been changed then.
/// </summary>
public sealed partial class Workspace
{
    private readonly IProcessLister _processes;
    private readonly TimeProvider? _time;
    private readonly IFaultHook? _faults;
    private readonly FlatpakCheck? _flatpak;

    private Workspace(
        LocalStore local, BootstrapFile bootstrap, IProcessLister processes, TimeProvider? time, IFaultHook? faults, FlatpakCheck? flatpak)
    {
        Local = local;
        Bootstrap = bootstrap;
        Shared = new SharedStore(bootstrap.SyncRoot);
        Journal = Journal.For(local);
        _processes = processes;
        _time = time;
        _faults = faults;
        _flatpak = flatpak;
    }

    public LocalStore Local { get; }

    public BootstrapFile Bootstrap { get; }

    public SharedStore Shared { get; }

    public Journal Journal { get; }

    public string DeviceName => Bootstrap.DeviceName;

    /// <summary>Opens this device's setup. Fails when "init" has not been run here.</summary>
    /// <param name="faults">Test seam, passed to the executor.</param>
    /// <param name="flatpak">The Flatpak precheck for apply; null where there are no Flatpaks.</param>
    public static Workspace Open(
        LocalStore local, IProcessLister processes, TimeProvider? time = null, IFaultHook? faults = null, FlatpakCheck? flatpak = null)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(processes);
        var bootstrap = local.TryReadBootstrap()
            ?? throw new StoreException(
                StoreProblem.NotFound,
                new Note("This device is not set up yet.", new NextStep(NextStepKind.SetUp)));
        return new Workspace(local, bootstrap, processes, time, faults, flatpak);
    }

    /// <summary>The device names that have a file in the sync root, for "init" to offer.</summary>
    public static IReadOnlyList<string> ExistingDevices(string syncRoot)
    {
        var shared = new SharedStore(syncRoot);
        return shared.IsInitialized ? shared.ListDeviceNames() : [];
    }

    /// <summary>
    /// "init": ties this install to a sync root and a device name (design 5.6, 5.12). Only
    /// <see cref="InitRequest.CreateRoot"/> creates the marker folder, so pointing a later device
    /// at an empty mount point fails instead of starting a second, empty setup.
    /// </summary>
    public static InitResult Init(LocalStore local, InitRequest request)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(request);

        var shared = new SharedStore(request.SyncRoot);
        if (!Identifiers.IsValidDeviceName(request.DeviceName))
        {
            throw new StoreException(
                StoreProblem.Invalid,
                $"'{request.DeviceName}' is not a valid device name. Use lowercase letters, digits, '-' and '_', for example 'deck'.");
        }

        // Before anything is written: a refusal here must leave the device as it was.
        var notes = GuardSyncRootChange(local, shared.SyncRoot);

        var createdRoot = false;
        if (!shared.IsInitialized)
        {
            if (!request.CreateRoot)
            {
                throw new StoreException(
                    StoreProblem.NotInitialized,
                    new Note(
                        $"'{shared.SyncRoot}' is not set up for SaveTether: the folder '{Path.Combine(SharedStore.ToolFolderName, SharedStore.MetaFolderName)}' is missing. " +
                        "On any device but the first, wait for the sync tool to deliver that folder; " +
                        "if the sync root is on a removable drive, check that it is connected.",
                        new NextStep(NextStepKind.CreateRoot)));
            }

            shared.CreateRoot();
            createdRoot = true;
        }

        var existing = shared.TryReadDevice(request.DeviceName);
        var backupRoot = request.BackupRoot ?? existing?.BackupRoot ?? request.DefaultBackupRoot;
        RequireUsableBackupLocation(backupRoot, shared);

        // Running init again on the install that already owns the name changes nothing about ownership.
        var alreadyOurs = existing is not null && local.TryReadBootstrap() is { } known
            && known.DeviceName == existing.DeviceName && known.InstallId == existing.InstallId;
        var device = alreadyOurs ? existing! : DeviceIdentity.Claim(shared, request.DeviceName, backupRoot, request.TakeOver);
        if (device.BackupRoot != backupRoot)
        {
            device = device with { BackupRoot = backupRoot };
            shared.WriteDevice(device);
        }

        if (SafeFs.Probe(PathRules.ResolveAll(backupRoot)) != EntryKind.Directory)
        {
            // A location the owner did not name here may be on a drive that is not connected.
            // Creating it then would put backups somewhere unexpected.
            var mayCreate = existing is null || request.BackupRoot is not null || PathRules.AreSame(backupRoot, request.DefaultBackupRoot);
            if (mayCreate)
            {
                Directory.CreateDirectory(backupRoot);
            }
            else
            {
                notes.Add(new Note(
                    $"The backup location '{backupRoot}' from this device's file does not exist. " +
                    "Connect its drive.",
                    new NextStep(NextStepKind.ChooseBackupLocation)));
            }
        }

        local.WriteBootstrap(new BootstrapFile
        {
            Schema = BootstrapFile.CurrentSchema, SyncRoot = shared.SyncRoot, DeviceName = device.DeviceName, InstallId = device.InstallId,
        });

        return new InitResult(
            shared.SyncRoot, device.DeviceName, backupRoot, createdRoot,
            NewDevice: existing is null, TookOver: existing is not null && !alreadyOurs,
            shared.ListDeviceNames(), notes);
    }

    /// <summary>
    /// "init" with a sync root other than this device's: refused while any of its save folders is
    /// still a link into the old sync root whose shared folder is there (desktop-app-spec.md,
    /// decision 9). Those links would point into a folder the device no longer tracks, so they are
    /// reverted first. A link whose shared folder cannot be reached holds no saves, and cannot be
    /// reverted, so it does not block; it comes back as a note.
    /// </summary>
    /// <remarks>
    /// The save folders come from this device's own record of what it applied, which is local and so
    /// readable with the old sync root unmounted, and from its file in the old sync root when that is
    /// readable, in case the record was lost.
    /// </remarks>
    private static List<Note> GuardSyncRootChange(LocalStore local, string newSyncRoot)
    {
        if (local.TryReadBootstrap() is not { } known || SameFolder(known.SyncRoot, newSyncRoot))
        {
            return [];
        }

        var sources = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, applied) in local.ReadState().Mappings)
        {
            sources[id] = applied.Source;
        }

        var oldShared = new SharedStore(known.SyncRoot);
        if (oldShared.IsInitialized && oldShared.TryReadDevice(known.DeviceName) is { } device)
        {
            foreach (var (id, path) in device.Paths)
            {
                sources.TryAdd(id, path.Source);
            }
        }

        var oldRoot = PathRules.ResolveAll(known.SyncRoot);
        var linked = new List<string>();
        var notes = new List<Note>();
        foreach (var (id, source) in sources)
        {
            if (!Path.IsPathFullyQualified(source) || !IsLink(source))
            {
                continue;
            }

            var target = SafeFs.ReadLinkTarget(source);
            if (!PathRules.IsSameOrInside(target, known.SyncRoot) && !PathRules.IsSameOrInside(target, oldRoot))
            {
                continue; // Not a link into the old sync root, so not one this change strands.
            }

            if (SafeFs.Probe(PathRules.ResolveAll(target)) == EntryKind.Directory)
            {
                linked.Add(id);
            }
            else
            {
                notes.Add(
                    $"'{source}' ({id}) is still a link to '{target}' in the old sync folder, which cannot be reached, " +
                    "so it holds none of the saves. Delete the link by hand before using that save folder again.");
            }
        }

        if (linked.Count > 0)
        {
            throw new StoreException(
                StoreProblem.MappingsApplied,
                new Note(
                    $"This device uses the sync folder '{known.SyncRoot}', and {string.Join(", ", linked)} " +
                    $"{(linked.Count == 1 ? "is" : "are")} linked into it. Changing the sync folder now would leave " +
                    $"{(linked.Count == 1 ? "that link" : "those links")} pointing into a folder this device no longer uses. Nothing was changed.",
                    linked.Select(id => new NextStep(NextStepKind.Revert, id)).ToList()));
        }

        return notes;
    }

    private static bool SameFolder(string a, string b) =>
        PathRules.AreSame(a, b) || PathRules.AreSame(PathRules.ResolveAll(a), PathRules.ResolveAll(b));

    /// <summary>"map add": the shared mapping, if it is new, and this device's save folder for it.</summary>
    /// <param name="entryDepth">How many levels down one game's saves sit (design 5.13), or null to leave it as it is.</param>
    public MapResult MapAdd(
        string mappingId, string? target, string source, IReadOnlyList<string> processNames, int? entryDepth = null)
    {
        ArgumentNullException.ThrowIfNull(processNames);
        var device = ReadOwnDevice();
        if (!Identifiers.IsValidMappingId(mappingId))
        {
            throw new StoreException(StoreProblem.Invalid, $"'{mappingId}' is not a valid mapping ID. Expected a form like 'ppsspp.savedata'.");
        }

        if (string.IsNullOrWhiteSpace(source) || !Path.IsPathFullyQualified(source))
        {
            throw new StoreException(StoreProblem.Invalid, $"The save folder must be an absolute path, but got '{source}'.");
        }

        var newSource = PathRules.Normalize(source);
        if (SourceOverlap(mappingId, newSource, SourcesOf(device)) is { } overlap)
        {
            throw new StoreException(overlap.Message, overlap.Facts);
        }

        if (device.Paths.TryGetValue(mappingId, out var current)
            && !PathRules.AreSame(current.Source, newSource) && IsLink(current.Source))
        {
            throw new StoreException(
                new Note(
                    $"'{mappingId}' is applied on this device at '{current.Source}', so its save folder cannot be changed.",
                    new NextStep(NextStepKind.Revert, mappingId)),
                new ConflictFacts(ConflictKind.AppliedElsewhere, mappingId, Folder: current.Source));
        }

        var mapping = Shared.TryReadMapping(mappingId);
        var created = mapping is null;
        if (mapping is null)
        {
            if (target is null)
            {
                throw new StoreException(
                    StoreProblem.Invalid,
                    new Note(
                        $"There is no mapping '{mappingId}' yet, so its shared folder has to be named, in the form <app>/<save folder>.",
                        new NextStep(NextStepKind.GiveTarget, mappingId)));
            }

            mapping = new MappingFile
            {
                Schema = MappingFile.CurrentSchema, Id = mappingId, Target = target, Mechanism = Mechanism.Link, EntryDepth = entryDepth,
            }.Normalized();
            Shared.WriteMapping(mapping);
        }
        else if (target is not null && target != mapping.Target)
        {
            throw new StoreException(
                $"Mapping '{mappingId}' already exists with target '{mapping.Target}'. Its target cannot be changed to '{target}'.",
                new ConflictFacts(ConflictKind.TargetDiffers, mappingId, Target: mapping.Target));
        }

        // Unlike the target, the entry depth of an existing mapping may be changed: nothing points at it.
        var depthChanged = !created && entryDepth is { } depth && depth != mapping.Depth;
        if (depthChanged)
        {
            mapping = (mapping with { EntryDepth = entryDepth }).Normalized();
            Shared.WriteMapping(mapping);
        }

        var path = new DevicePath { Source = newSource, ProcessNames = processNames.ToList() };
        Shared.WriteDevice(device with { Paths = new Dictionary<string, DevicePath>(device.Paths) { [mappingId] = path } });
        return new MapResult(mapping, path, created, depthChanged);
    }

    /// <summary>"map remove": drops this device's entry only. The shared mapping stays for the other devices.</summary>
    public DevicePath MapRemove(string mappingId)
    {
        var device = ReadOwnDevice();
        if (!device.Paths.TryGetValue(mappingId, out var path))
        {
            throw new StoreException(StoreProblem.NotFound, $"This device has no save folder set for '{mappingId}'.");
        }

        if (IsLink(path.Source))
        {
            throw new StoreException(
                new Note(
                    $"'{mappingId}' is applied on this device: '{path.Source}' is a link.",
                    new NextStep(NextStepKind.Revert, mappingId)),
                new ConflictFacts(ConflictKind.AppliedElsewhere, mappingId, Folder: path.Source));
        }

        var paths = new Dictionary<string, DevicePath>(device.Paths);
        paths.Remove(mappingId);
        Shared.WriteDevice(device with { Paths = paths });
        return path;
    }

    /// <summary>
    /// "status": every mapping in the shared list, or one, as it stands on this device. When the sync
    /// root is unmounted or not set up, it still reports, from this device's own record of what it
    /// applied: those links are Broken then, and that is what the owner most needs to see.
    /// </summary>
    public StatusReport Status(string? mappingId = null)
    {
        if (!Shared.IsInitialized)
        {
            var problem = Shared.NotAvailableMessage;
            var recorded = Local.ReadState().Mappings;
            if (mappingId is not null && !recorded.ContainsKey(mappingId))
            {
                throw new StoreException(StoreProblem.NotInitialized, problem);
            }

            return new StatusReport(
                DeviceName, Shared.SyncRoot, DeviceFileMismatch: false, Journal.IsOpen,
                recorded
                    .Where(r => mappingId is null || r.Key == mappingId)
                    .OrderBy(r => r.Key, StringComparer.Ordinal)
                    .Select(r => HealthCheck.CheckRecorded(r.Key, r.Value))
                    .ToList(),
                problem);
        }

        var device = Shared.TryReadDevice(DeviceName)
            ?? throw new StoreException(
                StoreProblem.NotFound,
                new Note($"The sync root has no file for this device ('{DeviceName}').", new NextStep(NextStepKind.SetUpAgain)));
        var state = Local.ReadState();
        var mappings = mappingId is null ? Shared.ListMappings() : [ReadMapping(mappingId)];
        return new StatusReport(
            DeviceName, Shared.SyncRoot, !DeviceIdentity.Matches(Bootstrap, device), Journal.IsOpen,
            mappings
                .Select(m => HealthCheck.Check(Shared, m, device.Paths.GetValueOrDefault(m.Id), state.Mappings.GetValueOrDefault(m.Id)))
                .ToList());
    }

    /// <summary>The mappings this device has a save folder for, in order: what "apply --all" runs.</summary>
    public IReadOnlyList<string> MappingsWithPaths() => ReadOwnDevice().Paths.Keys.Order(StringComparer.Ordinal).ToList();

    public PlanOutcome PlanApply(string mappingId, ApplyRequest request) => Planner.Plan(WithSandbox(ReadSnapshot(mappingId)), request);

    /// <summary>Adds the Flatpak precheck: only apply makes a link the app must follow.</summary>
    private Snapshot WithSandbox(Snapshot s) =>
        _flatpak is null ? s : s with { Sandbox = _flatpak.Check(s.Source, s.Target, s.ResolvedTarget, Shared.DataRoot, s.DataRoot) };

    /// <summary>The Flatpak precheck for one mapping on its own; null when its save folder is not in a Flatpak app.</summary>
    public SandboxReach? CheckSandbox(string mappingId) => _flatpak is null ? null : WithSandbox(ReadSnapshot(mappingId)).Sandbox;

    /// <summary>
    /// Grants a Flatpak app write access to the savetether folder ("flatpak override --user"). It
    /// changes that app's settings: call it only once the owner has agreed. True when it succeeded.
    /// </summary>
    public bool GrantSandbox(SandboxReach reach) => _flatpak?.Grant(reach) ?? false;

    /// <summary>
    /// After a revert: SaveTether's grant to the Flatpak app holding this mapping's save folder,
    /// when no other mapping applied on this device still links from inside that app. Null otherwise.
    /// </summary>
    public SandboxGrant? SandboxGrantNoLongerNeeded(string mappingId)
    {
        if (_flatpak is null)
        {
            return null;
        }

        var device = ReadOwnDevice();
        if (!device.Paths.TryGetValue(mappingId, out var path) || _flatpak.AppOwning(path.Source) is not { } appId)
        {
            return null;
        }

        var stillUsed = device.Paths.Any(other =>
            other.Key != mappingId && _flatpak.AppOwning(other.Value.Source) == appId && IsLink(other.Value.Source));
        return stillUsed ? null : _flatpak.FindGrant(path.Source, Shared.DataRoot, PathRules.ResolveAll(Shared.DataRoot));
    }

    /// <summary>Takes back SaveTether's grant to a Flatpak app. Call it only once the owner has agreed. True when it succeeded.</summary>
    public bool RevokeSandbox(SandboxGrant grant) => _flatpak?.Revoke(grant) ?? false;

    public PlanOutcome PlanRevert(string mappingId, bool assumeClosed) => Planner.PlanRevert(ReadSnapshot(mappingId), assumeClosed);

    public PlanOutcome PlanRestore(string backupId, RestoreRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupId);
        var device = ReadOwnDevice();
        var processNames = device.Paths.GetValueOrDefault(backupId.Split('/')[0])?.ProcessNames ?? [];
        return RestorePlanner.Plan(OpenBackups(device), Shared, backupId, request, processNames, _processes, Journal.IsOpen);
    }

    /// <summary>Runs a plan made by one of the Plan methods.</summary>
    /// <param name="progress">Told as each step starts and finishes; see <see cref="Executor.Run"/>.</param>
    public ExecutionResult Run(Plan plan, IProgress<StepProgress>? progress = null) => NewExecutor(ReadOwnDevice()).Run(plan, progress);

    /// <summary>"recover". Works without the backup location and without checking who owns the device name.</summary>
    /// <param name="progress">Told about the steps when the operation is carried through; see <see cref="Executor.Recover"/>.</param>
    public ExecutionResult Recover(IProgress<StepProgress>? progress = null)
    {
        var device = Shared.IsInitialized ? Shared.TryReadDevice(DeviceName) : null;
        return NewExecutor(device).Recover(progress);
    }

    /// <summary>"backup list".</summary>
    public IReadOnlyList<BackupInfo> ListBackups(string? mappingId = null) => OpenBackups(ReadOwnDevice()).List(mappingId);

    /// <summary>"backup delete", before asking: the backup, and whether it is its mapping's first. Reads only.</summary>
    public BackupDeletion DescribeBackupDeletion(string backupId) => OpenBackups(ReadOwnDevice()).Describe(backupId);

    /// <summary>
    /// "backup delete": removes one backup. Refused while an operation is unfinished, since recovering it may
    /// need the backups it took. Call it under the operation lock, once the owner has confirmed.
    /// </summary>
    public BackupDeletion DeleteBackup(string backupId)
    {
        if (Journal.IsOpen)
        {
            throw new BackupException(
                $"No backup is deleted while an operation is unfinished: recovering it may need its backups. Recover first, then delete '{backupId}'.");
        }

        return OpenBackups(ReadOwnDevice()).Remove(backupId);
    }

    private Executor NewExecutor(DeviceFile? device)
    {
        BackupStore? backups = null;
        if (device is not null)
        {
            try
            {
                backups = OpenBackups(device);
            }
            catch (BackupException)
            {
                // Steps that need it will say so. Verifying an existing link and recovery do not.
            }
        }

        return new Executor(Shared, Local, backups, DeviceName, _time, _faults);
    }

    private BackupStore OpenBackups(DeviceFile device) => new(device.BackupRoot, Shared.SyncRoot, _time);

    private Snapshot ReadSnapshot(string mappingId)
    {
        var device = ReadOwnDevice();
        var mapping = ReadMapping(mappingId);
        if (!device.Paths.TryGetValue(mappingId, out var path))
        {
            throw new StoreException(
                StoreProblem.NotFound,
                new Note($"This device has no save folder set for '{mappingId}'.", new NextStep(NextStepKind.SetSaveFolder, mappingId)));
        }

        return SnapshotReader.Read(Shared, mapping, DeviceName, path, device.BackupRoot, _processes, Journal.IsOpen);
    }

    private MappingFile ReadMapping(string mappingId) =>
        Shared.TryReadMapping(mappingId)
        ?? throw new StoreException(
            StoreProblem.NotFound,
            new Note($"There is no mapping '{mappingId}'.", new NextStep(NextStepKind.AddMapping, mappingId)));

    /// <summary>
    /// This device's shared file, refused when another install owns the name now: acting on it
    /// would apply another machine's paths (design 5.6).
    /// </summary>
    private DeviceFile ReadOwnDevice()
    {
        var device = Shared.TryReadDevice(DeviceName)
            ?? throw new StoreException(
                StoreProblem.NotFound,
                new Note($"The sync root has no file for this device ('{DeviceName}').", new NextStep(NextStepKind.SetUpAgain)));
        if (!DeviceIdentity.Matches(Bootstrap, device))
        {
            throw new StoreException(
                StoreProblem.NameInUse,
                new Note(
                    $"The device name '{DeviceName}' now belongs to another install: another machine may be using it, or took it over.",
                    new NextStep(NextStepKind.TakeOverName)));
        }

        return device;
    }

    private static void RequireUsableBackupLocation(string backupRoot, SharedStore shared)
    {
        if (!Path.IsPathFullyQualified(backupRoot))
        {
            throw new BackupException($"The backup location must be an absolute path, but got '{backupRoot}'.");
        }

        var resolved = PathRules.ResolveAll(backupRoot);
        var syncRoot = PathRules.ResolveAll(shared.SyncRoot);
        if (PathRules.IsSameOrInside(resolved, syncRoot) || PathRules.IsSameOrInside(syncRoot, resolved))
        {
            throw new BackupException(
                $"The backup location '{backupRoot}' must be separate from the sync root: backups inside it would be deleted along with the saves they protect.");
        }
    }

    /// <summary>
    /// Why a save folder cannot be used for a mapping because another mapping on this device already
    /// has it, has a folder inside it, or has one containing it; null when none does. One folder can be
    /// linked to only one shared folder, and a link inside another mapping's folder would be inside
    /// that mapping's shared copy. Links in the folders leading to each path are followed, so one
    /// folder reached two ways is still one folder; the save folder itself is not, since it is often a link.
    /// </summary>
    /// <param name="others">The other mappings' save folders, by mapping ID.</param>
    internal static (Note Message, ConflictFacts Facts)? SourceOverlap(
        string mappingId, string source, IEnumerable<KeyValuePair<string, string>> others)
    {
        var mine = PathRules.ResolveParents(source);
        foreach (var (otherId, otherSource) in others)
        {
            if (otherId == mappingId || !Path.IsPathFullyQualified(otherSource))
            {
                continue;
            }

            var theirs = PathRules.ResolveParents(otherSource);
            var how = PathRules.AreSame(mine, theirs) ? "is already the save folder of"
                : PathRules.IsSameOrInside(mine, theirs) ? "is inside the save folder of"
                : PathRules.IsSameOrInside(theirs, mine) ? "contains the save folder of"
                : null;
            if (how is not null)
            {
                return (
                    new Note(
                        $"'{source}' {how} '{otherId}' on this device ('{otherSource}'). A folder can be shared through one mapping only.",
                        new NextStep(NextStepKind.RemoveMapping, otherId)),
                    new ConflictFacts(ConflictKind.FolderInUse, otherId, Folder: otherSource));
            }
        }

        return null;
    }

    private static IEnumerable<KeyValuePair<string, string>> SourcesOf(DeviceFile device) =>
        device.Paths.Select(p => new KeyValuePair<string, string>(p.Key, p.Value.Source));

    private static bool IsLink(string path) =>
        Path.IsPathFullyQualified(path) && SafeFs.Probe(path) is EntryKind.Symlink or EntryKind.Junction;
}
