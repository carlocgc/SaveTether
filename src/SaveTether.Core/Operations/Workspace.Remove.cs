using System.Text.Json.Serialization;
using SaveTether.Core.Backup;
using SaveTether.Core.Execution;
using SaveTether.Core.FileSystem;
using SaveTether.Core.Model;
using SaveTether.Core.Planning;
using SaveTether.Core.Storage;

namespace SaveTether.Core.Operations;

/// <summary>What to do with a mapping that only the device being removed uses (phase 3.6, D5).</summary>
public enum OrphanChoice
{
    /// <summary>Leave the mapping and its saves in the sync folder. The default.</summary>
    [JsonStringEnumMemberName("keep")]
    Keep,

    /// <summary>Delete the mapping's record; its shared save folder stays.</summary>
    [JsonStringEnumMemberName("delete-mapping")]
    DeleteMapping,

    /// <summary>Delete the mapping and its shared save folder, as "map delete" does, after a backup.</summary>
    [JsonStringEnumMemberName("delete-with-saves")]
    DeleteWithSaves,
}

/// <summary>Why this install is flagged as removed (phase 3.6, D6).</summary>
public enum RemovalKind
{
    /// <summary>The tombstone says another device removed this one.</summary>
    [JsonStringEnumMemberName("removed-by-another")]
    RemovedByAnother,

    /// <summary>This device started its own removal and it did not finish.</summary>
    [JsonStringEnumMemberName("unfinished")]
    Unfinished,
}

/// <summary>What the owner chose for a removal. The choices are not stored: a run again is given them again.</summary>
/// <param name="Orphans">A choice per mapping ID; one not named is kept.</param>
/// <param name="DeleteBackups">Delete every backup of this device too. Kept otherwise.</param>
public sealed record RemovalChoices(IReadOnlyDictionary<string, OrphanChoice>? Orphans = null, bool DeleteBackups = false);

public sealed record OrphanRemoval(string MappingId, OrphanChoice Choice);

/// <summary>What "Remove this device" would do. Made without touching the disk.</summary>
/// <param name="Reverts">The links that become real folders, each a revert plan whose source is the folder given back.</param>
/// <param name="NotLinked">Mappings this device has a folder for that are not linked here: skipped.</param>
/// <param name="Refusals">Mappings whose revert is refused, each with its own reason and fix. Nothing runs while there are any.</param>
/// <param name="Orphans">Mappings only this device uses, with the choice for each.</param>
/// <param name="SharedFiles">The device's own files in the sync folder: its seed markers and its device file.</param>
/// <param name="Backups">The backups that exist now and will be deleted; empty when they are kept.</param>
/// <param name="LocalFiles">The files in this device's local folder that go; the bootstrap is last.</param>
public sealed record DeviceRemovalPlan(
    string DeviceName,
    IReadOnlyList<Plan> Reverts,
    IReadOnlyList<string> NotLinked,
    IReadOnlyList<Refusal> Refusals,
    IReadOnlyList<OrphanRemoval> Orphans,
    IReadOnlyList<string> SharedFiles,
    IReadOnlyList<BackupInfo> Backups,
    bool DeleteBackups,
    IReadOnlyList<string> LocalFiles)
{
    public bool IsRefused => Refusals.Count > 0;

    /// <summary>The save folders that become real folders: the owner's from now on.</summary>
    public IReadOnlyList<string> GivenBack => Reverts.Select(r => r.Source).ToList();
}

/// <summary>What a removal did. When <see cref="Failure"/> is set a revert did not finish and nothing shared was deleted.</summary>
public sealed record DeviceRemovalResult(
    IReadOnlyList<string> GivenBack,
    IReadOnlyList<OrphanRemoval> OrphansDeleted,
    int BackupsDeleted,
    ExecutionResult? Failure);

public sealed partial class Workspace
{
    private static readonly string[] LocalFileNames =
        ["state.json", Execution.Journal.FileName, OperationLock.HolderFileName, "bootstrap.json"];

    /// <summary>
    /// "device remove", before asking (phase 3.6, T5). Reads only. Refused (a <see cref="StoreException"/>) when an operation is
    /// unfinished, the sync root cannot be read, this device's file belongs to another install, or a choice names a mapping that
    /// another device uses. A mapping whose revert is refused is in <see cref="DeviceRemovalPlan.Refusals"/>.
    /// </summary>
    public DeviceRemovalPlan PlanDeviceRemoval(RemovalChoices choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        _finishingRemoval = true;
        try
        {
            return PlanRemoval(choices);
        }
        finally
        {
            _finishingRemoval = false;
        }
    }

    private DeviceRemovalPlan PlanRemoval(RemovalChoices choices)
    {
        if (Journal.IsOpen)
        {
            throw new StoreException(
                StoreProblem.Conflict,
                new Note("An earlier operation did not finish. Recover it first.", new NextStep(NextStepKind.Recover)));
        }

        var device = ReadOwnDevice();
        if (!DeviceIdentity.Matches(Bootstrap, device))
        {
            throw new StoreException(
                StoreProblem.Conflict,
                $"The device file '{DeviceName}' belongs to another install, so this device cannot remove it. Run 'status' for what to do.");
        }

        var usedByOthers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in Shared.ListDeviceNames().Where(n => n != DeviceName))
        {
            usedByOthers.UnionWith(Shared.ReadDevice(name).Paths.Keys);
        }

        // A mapping already deleted by an earlier run has no file and is no longer listed; one that is still there and
        // is used by no other device is an orphan, even once this device's folder for it has been dropped.
        var orphans = new List<OrphanRemoval>();
        var named = choices.Orphans ?? new Dictionary<string, OrphanChoice>();
        // A choice for a mapping that is gone is fine only on a run again: this install's tombstone says an earlier run got that far.
        var earlierRun = Shared.ReadTombstones().Any(t => t.InstallId == Bootstrap.InstallId);
        foreach (var id in named.Keys.Order(StringComparer.Ordinal))
        {
            if (Shared.TryReadMapping(id) is null)
            {
                if (!earlierRun)
                {
                    throw new StoreException(StoreProblem.NotFound, $"There is no mapping '{id}' in the sync folder, so it cannot be deleted. Check the ID.");
                }
            }
            else if (usedByOthers.Contains(id))
            {
                throw new StoreException(StoreProblem.Conflict, $"'{id}' is also used by another device, so it is not deleted here.");
            }
        }

        foreach (var id in device.Paths.Keys.Concat(named.Keys).Distinct().Order(StringComparer.Ordinal))
        {
            if (!usedByOthers.Contains(id) && Shared.TryReadMapping(id) is not null)
            {
                orphans.Add(new OrphanRemoval(id, named.GetValueOrDefault(id, OrphanChoice.Keep)));
            }
        }

        var reverts = new List<Plan>();
        var notLinked = new List<string>();
        var refusals = new List<Refusal>();
        foreach (var id in device.Paths.Keys.Order(StringComparer.Ordinal))
        {
            var outcome = PlanRevert(id, assumeClosed: false);
            if (outcome.Plan is { } plan)
            {
                reverts.Add(plan);
            }
            else if (outcome.Refusal is { } refusal && refusal.Failures.Count > 0 && refusal.Failures.All(f => f.Kind == PrecheckKind.NotApplied))
            {
                notLinked.Add(id);
            }
            else if (outcome.Refusal is { } other)
            {
                refusals.Add(other);
            }
        }

        var backups = choices.DeleteBackups ? ListBackups() : [];
        var local = LocalFileNames.Where(f => SafeFs.Probe(Path.Combine(Local.Folder, f)) == EntryKind.File).ToList();
        return new DeviceRemovalPlan(
            DeviceName, reverts, notLinked, refusals, orphans, Shared.FilesOf(DeviceName), backups, choices.DeleteBackups, local);
    }

    /// <summary>
    /// "device remove": takes this device out of the sync folder (phase 3.6, T5). Each step can be run again, and the bootstrap
    /// goes last as the commit point: until then the device still counts as set up. The order is: revert every link here (each its
    /// own journalled operation); carry out the orphan choices; write the tombstone; delete this device's markers and file;
    /// delete the backups if chosen; delete the local state. Nothing shared is deleted until every revert is done. The caller holds
    /// the operation lock for the whole removal and has the owner's confirmation.
    /// </summary>
    /// <param name="progress">Told as each step of each revert starts and finishes.</param>
    public DeviceRemovalResult RemoveDevice(DeviceRemovalPlan plan, IProgress<StepProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        _finishingRemoval = true;
        try
        {
            return RunRemoval(plan, progress);
        }
        finally
        {
            _finishingRemoval = false;
        }
    }

    private DeviceRemovalResult RunRemoval(DeviceRemovalPlan plan, IProgress<StepProgress>? progress)
    {
        if (plan.IsRefused)
        {
            throw new StoreException(StoreProblem.Conflict, "A link here cannot be reverted, so the device is not removed. Nothing was changed.");
        }

        if (Journal.IsOpen)
        {
            throw new StoreException(
                StoreProblem.Conflict,
                new Note("An earlier operation did not finish. Recover it first.", new NextStep(NextStepKind.Recover)));
        }

        var device = ReadOwnDevice();
        foreach (var revert in plan.Reverts)
        {
            var result = Run(revert, progress);
            if (result.Status != ExecutionStatus.Completed)
            {
                return new DeviceRemovalResult([], [], 0, result);
            }
        }

        var deleted = new List<OrphanRemoval>();
        foreach (var orphan in plan.Orphans.Where(o => o.Choice != OrphanChoice.Keep))
        {
            if (Shared.TryReadMapping(orphan.MappingId) is null)
            {
                continue; // Already deleted by an earlier run.
            }

            // This device's folder for it must go first: a mapping is unused only when no device file has a path for it.
            var current = ReadOwnDevice();
            if (current.Paths.ContainsKey(orphan.MappingId))
            {
                var paths = new Dictionary<string, DevicePath>(current.Paths);
                paths.Remove(orphan.MappingId);
                Shared.WriteDevice(current with { Paths = paths });
            }

            if (orphan.Choice == OrphanChoice.DeleteWithSaves)
            {
                DeleteMapping(orphan.MappingId);
            }
            else
            {
                CheckMappingUnused(orphan.MappingId);
                Shared.RetireMapping(orphan.MappingId);
            }

            deleted.Add(orphan);
        }

        // Before anything shared is deleted: a backup root that fails here leaves the device still set up, so this can be run again.
        // The backups made by the reverts go too, so what is deleted is what exists now, not what the plan listed.
        var backupsDeleted = plan.DeleteBackups ? OpenBackups(device).RemoveMany(null, BackupCleanup.All).Count : 0;

        // Kept as it is when another device (or an earlier run) wrote it: it says who removed this device.
        if (!Shared.ReadTombstones().Any(t => t.InstallId == Bootstrap.InstallId))
        {
            Shared.WriteTombstone(new TombstoneFile
            {
                Schema = TombstoneFile.CurrentSchema,
                DeviceName = DeviceName,
                InstallId = Bootstrap.InstallId,
                RemovedUtc = (_time ?? TimeProvider.System).GetUtcNow(),
                RemovedBy = DeviceName,
            });
        }

        foreach (var mapping in Shared.ListMappings())
        {
            Shared.DeleteSeedMarker(mapping.Id, DeviceName);
        }

        if (Shared.TryReadDevice(DeviceName) is not null)
        {
            Shared.DeleteDevice(DeviceName);
        }

        Local.DeleteDeviceFiles(Shared.SyncRoot);
        return new DeviceRemovalResult(plan.GivenBack, deleted, backupsDeleted, null);
    }

    private bool _finishingRemoval;

    /// <summary>
    /// While a removal is planned or run: this device's file as it was, rebuilt from the local record, when another device removed
    /// this one (or an earlier run deleted the file) and this install's tombstone says so. The backup root is then the default one,
    /// since the file that named it is gone; the app's process names are not known, so a running app is not noticed.
    /// </summary>
    private DeviceFile? RemovedDeviceFile()
    {
        if (!_finishingRemoval || !Shared.ReadTombstones().Any(t => t.InstallId == Bootstrap.InstallId))
        {
            return null;
        }

        // A revert refuses a backup folder that is not there; this one only holds what the revert backs up.
        var backupRoot = Path.Combine(Local.Folder, "backups");
        Directory.CreateDirectory(backupRoot);
        return new DeviceFile
        {
            Schema = DeviceFile.CurrentSchema,
            DeviceName = DeviceName,
            InstallId = Bootstrap.InstallId,
            BackupRoot = backupRoot,
            Paths = Local.ReadState().Mappings.ToDictionary(m => m.Key, m => new DevicePath { Source = m.Value.Source }),
        };
    }

    /// <summary>
    /// "device remove --other": deletes a device that is gone (the old PC): its seed markers and its file, after writing its tombstone.
    /// Nobody's links are touched. Returns the files deleted (with <paramref name="dryRun"/>, the ones that would be). Refused for this
    /// device, and for a file that carries this install's ID (left by an unfinished rename: a tombstone for it would make this
    /// device think it had been removed).
    /// </summary>
    public IReadOnlyList<string> RemoveOtherDevice(string name, bool dryRun = false)
    {
        if (!Identifiers.IsValidDeviceName(name))
        {
            throw new StoreException(StoreProblem.Invalid, $"'{name}' is not a valid device name.");
        }

        if (name == DeviceName)
        {
            throw new StoreException(StoreProblem.Conflict, "That is this device. Remove it without naming it, not as another device.");
        }

        var device = Shared.TryReadDevice(name)
            ?? throw new StoreException(StoreProblem.NotFound, $"There is no device '{name}' in the sync folder. Check the name against the devices in the sync folder.");
        if (device.InstallId == Bootstrap.InstallId)
        {
            throw new StoreException(
                StoreProblem.Conflict,
                $"'{name}' carries this install's ID, so it is a file left by an unfinished rename, not another device. Renaming this device again clears it.");
        }

        var files = Shared.FilesOf(name);
        if (dryRun)
        {
            return files;
        }

        Shared.WriteTombstone(new TombstoneFile
        {
            Schema = TombstoneFile.CurrentSchema,
            DeviceName = name,
            InstallId = device.InstallId,
            RemovedUtc = (_time ?? TimeProvider.System).GetUtcNow(),
            RemovedBy = DeviceName,
        });
        foreach (var mapping in Shared.ListMappings())
        {
            Shared.DeleteSeedMarker(mapping.Id, name);
        }

        Shared.DeleteDevice(name);
        return files;
    }
}
