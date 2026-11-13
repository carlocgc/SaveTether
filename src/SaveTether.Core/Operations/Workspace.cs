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
    IReadOnlyList<string> Devices, IReadOnlyList<string> Notes);

/// <param name="CreatedMapping">False when the shared mapping already existed and only this device's path was set.</param>
/// <param name="EntryDepthChanged">True when an existing mapping's entry depth was changed by this call.</param>
public sealed record MapResult(MappingFile Mapping, DevicePath Path, bool CreatedMapping, bool EntryDepthChanged);

public sealed record StatusReport(
    string DeviceName, string SyncRoot, bool DeviceFileMismatch, bool JournalOpen, IReadOnlyList<MappingHealth> Mappings);

/// <summary>
/// This device, set up: its local files, the shared files, and the operations the commands
/// run on them. The command line only parses arguments, calls this, and prints the result.
/// A refusal that is not a planner refusal is thrown as a <see cref="StoreException"/> or one of
/// the other I/O exceptions, with a message meant for the owner; nothing has been changed then.
/// </summary>
public sealed class Workspace
{
    private readonly IProcessLister _processes;
    private readonly TimeProvider? _time;
    private readonly IFaultHook? _faults;

    private Workspace(LocalStore local, BootstrapFile bootstrap, IProcessLister processes, TimeProvider? time, IFaultHook? faults)
    {
        Local = local;
        Bootstrap = bootstrap;
        Shared = new SharedStore(bootstrap.SyncRoot);
        Journal = Journal.For(local);
        _processes = processes;
        _time = time;
        _faults = faults;
    }

    public LocalStore Local { get; }

    public BootstrapFile Bootstrap { get; }

    public SharedStore Shared { get; }

    public Journal Journal { get; }

    public string DeviceName => Bootstrap.DeviceName;

    /// <summary>Opens this device's setup. Fails when "init" has not been run here.</summary>
    /// <param name="faults">Test seam, passed to the executor.</param>
    public static Workspace Open(LocalStore local, IProcessLister processes, TimeProvider? time = null, IFaultHook? faults = null)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(processes);
        var bootstrap = local.TryReadBootstrap()
            ?? throw new StoreException(
                StoreProblem.NotFound,
                "This device is not set up yet. Run 'savetether init --sync-root <path>' first.");
        return new Workspace(local, bootstrap, processes, time, faults);
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

        var createdRoot = false;
        if (!shared.IsInitialized)
        {
            if (!request.CreateRoot)
            {
                throw new StoreException(
                    StoreProblem.NotInitialized,
                    $"'{shared.SyncRoot}' is not set up for SaveTether: the folder '{Path.Combine(SharedStore.ToolFolderName, SharedStore.MetaFolderName)}' is missing. " +
                    "On the first device, add --create-root. On any other device, wait for the sync tool to deliver that folder; " +
                    "if the sync root is on a removable drive, check that it is connected.");
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

        var notes = new List<string>();
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
                notes.Add(
                    $"The backup location '{backupRoot}' from this device's file does not exist. " +
                    "Connect its drive, or run init again with --backup-root <path>.");
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
        if (device.Paths.TryGetValue(mappingId, out var current)
            && !PathRules.AreSame(current.Source, newSource) && IsLink(current.Source))
        {
            throw new StoreException(
                StoreProblem.Conflict,
                $"'{mappingId}' is applied on this device at '{current.Source}'. Run 'savetether revert {mappingId}' before changing its save folder.");
        }

        var mapping = Shared.TryReadMapping(mappingId);
        var created = mapping is null;
        if (mapping is null)
        {
            if (target is null)
            {
                throw new StoreException(
                    StoreProblem.Invalid,
                    $"There is no mapping '{mappingId}' yet, so it needs --target <app>/<save folder>, for example --target {Identifiers.AppOf(mappingId)}/saves.");
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
                StoreProblem.Conflict,
                $"Mapping '{mappingId}' already exists with target '{mapping.Target}'. Its target cannot be changed to '{target}'.");
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
                StoreProblem.Conflict,
                $"'{mappingId}' is applied on this device: '{path.Source}' is a link. Run 'savetether revert {mappingId}' first.");
        }

        var paths = new Dictionary<string, DevicePath>(device.Paths);
        paths.Remove(mappingId);
        Shared.WriteDevice(device with { Paths = paths });
        return path;
    }

    /// <summary>"status": every mapping in the shared list, or one, as it stands on this device.</summary>
    public StatusReport Status(string? mappingId = null)
    {
        var device = Shared.TryReadDevice(DeviceName)
            ?? throw new StoreException(
                StoreProblem.NotFound,
                $"The sync root has no file for this device ('{DeviceName}'). Run 'savetether init' again.");
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

    public PlanOutcome PlanApply(string mappingId, ApplyRequest request) => Planner.Plan(ReadSnapshot(mappingId), request);

    public PlanOutcome PlanRevert(string mappingId, bool assumeClosed) => Planner.PlanRevert(ReadSnapshot(mappingId), assumeClosed);

    public PlanOutcome PlanRestore(string backupId, RestoreRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupId);
        var device = ReadOwnDevice();
        var processNames = device.Paths.GetValueOrDefault(backupId.Split('/')[0])?.ProcessNames ?? [];
        return RestorePlanner.Plan(OpenBackups(device), Shared, backupId, request, processNames, _processes, Journal.IsOpen);
    }

    /// <summary>Runs a plan made by one of the Plan methods.</summary>
    public ExecutionResult Run(Plan plan) => NewExecutor(ReadOwnDevice()).Run(plan);

    /// <summary>"recover". Works without the backup location and without checking who owns the device name.</summary>
    public ExecutionResult Recover()
    {
        var device = Shared.IsInitialized ? Shared.TryReadDevice(DeviceName) : null;
        return NewExecutor(device).Recover();
    }

    /// <summary>"backup list".</summary>
    public IReadOnlyList<BackupInfo> ListBackups(string? mappingId = null) => OpenBackups(ReadOwnDevice()).List(mappingId);

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
                $"This device has no save folder set for '{mappingId}'. Set one with 'savetether map add {mappingId} --source <path>'.");
        }

        return SnapshotReader.Read(Shared, mapping, DeviceName, path, device.BackupRoot, _processes, Journal.IsOpen);
    }

    private MappingFile ReadMapping(string mappingId) =>
        Shared.TryReadMapping(mappingId)
        ?? throw new StoreException(
            StoreProblem.NotFound,
            $"There is no mapping '{mappingId}'. Add it with 'savetether map add {mappingId} --target <app>/<save folder> --source <path>'.");

    /// <summary>
    /// This device's shared file, refused when another install owns the name now: acting on it
    /// would apply another machine's paths (design 5.6).
    /// </summary>
    private DeviceFile ReadOwnDevice()
    {
        var device = Shared.TryReadDevice(DeviceName)
            ?? throw new StoreException(
                StoreProblem.NotFound,
                $"The sync root has no file for this device ('{DeviceName}'). Run 'savetether init' again.");
        if (!DeviceIdentity.Matches(Bootstrap, device))
        {
            throw new StoreException(
                StoreProblem.NameInUse,
                $"The device name '{DeviceName}' now belongs to another install: another machine may be using it, or took it over. " +
                "If this machine should have the name, run 'savetether init' again with --take-over; otherwise choose another name.");
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

    private static bool IsLink(string path) =>
        Path.IsPathFullyQualified(path) && SafeFs.Probe(path) is EntryKind.Symlink or EntryKind.Junction;
}
