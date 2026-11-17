using System.Text.Json.Serialization;
using SaveTether.Core.Backup;
using SaveTether.Core.FileSystem;
using SaveTether.Core.Planning;
using SaveTether.Core.Storage;

namespace SaveTether.Core.Operations;

/// <summary>What happened to one mapping in a snapshot run.</summary>
public enum SnapshotStatus
{
    /// <summary>A new snapshot was taken.</summary>
    [JsonStringEnumMemberName("taken")]
    Taken,

    /// <summary>Nothing changed since the newest snapshot, so no new one was kept.</summary>
    [JsonStringEnumMemberName("unchanged")]
    Unchanged,

    /// <summary>Dry run: a snapshot would be taken.</summary>
    [JsonStringEnumMemberName("planned")]
    Planned,

    /// <summary>Not applied on this device, so not snapshotted here. Not a problem.</summary>
    [JsonStringEnumMemberName("not-applied")]
    NotApplied,

    /// <summary>Not snapshotted, for the reason given: shared folder missing, not enough space, no such mapping.</summary>
    [JsonStringEnumMemberName("skipped")]
    Skipped,

    /// <summary>The copy was tried and did not verify; nothing was kept.</summary>
    [JsonStringEnumMemberName("failed")]
    Failed,
}

/// <param name="BackupId">The snapshot taken, or the unchanged one kept; null otherwise.</param>
/// <param name="Bytes">The shared folder's size now.</param>
/// <param name="Pruned">Older snapshots deleted to keep the retention count.</param>
/// <param name="KeptBytes">Space the snapshots of this mapping use after this run.</param>
/// <param name="KeptCount">How many snapshots of this mapping there are after this run.</param>
public sealed record SnapshotOutcome(
    string MappingId,
    SnapshotStatus Status,
    string? BackupId,
    long Bytes,
    int Files,
    IReadOnlyList<string> Pruned,
    long KeptBytes,
    int KeptCount,
    string? Message);

/// <param name="FreeBytes">Free space where backups are kept, after the run; null when it could not be read.</param>
public sealed record SnapshotReport(string BackupRoot, int Keep, long? FreeBytes, IReadOnlyList<SnapshotOutcome> Mappings);

public sealed partial class Workspace
{
    /// <summary>Snapshots kept per mapping when the owner does not say.</summary>
    public const int DefaultSnapshotKeep = 5;

    /// <summary>Space left free on the backup volume after a snapshot, so a snapshot never fills it.</summary>
    private const long SnapshotSpaceMargin = 64L * 1024 * 1024;

    /// <summary>
    /// Copies the shared folder of each mapping applied on this device into the backup store
    /// (design 5.4: optional snapshots), and keeps the newest <paramref name="keep"/> per mapping.
    /// Only reads the shared folders. Refused while an earlier operation is unfinished.
    /// </summary>
    /// <param name="mappingId">One mapping, or null for every mapping applied here.</param>
    public SnapshotReport Snapshot(string? mappingId, int keep, bool dryRun)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(keep, 1);
        if (Journal.IsOpen)
        {
            throw new StoreException(StoreProblem.Conflict, "An earlier operation did not finish. Run 'savetether recover' first.");
        }

        var device = ReadOwnDevice();
        var store = OpenBackups(device);
        var ids = mappingId is null
            ? device.Paths.Keys.Order(StringComparer.Ordinal).ToList()
            : [mappingId];

        var outcomes = new List<SnapshotOutcome>();
        foreach (var id in ids)
        {
            outcomes.Add(SnapshotOne(store, device.Paths.GetValueOrDefault(id)?.Source, id, keep, dryRun));
        }

        return new SnapshotReport(store.Root, keep, SnapshotReader.FreeBytesAt(store.Root), outcomes);
    }

    private SnapshotOutcome SnapshotOne(BackupStore store, string? source, string mappingId, int keep, bool dryRun)
    {
        SnapshotOutcome Outcome(SnapshotStatus status, string? message, string? backupId = null, long bytes = 0, int files = 0, IReadOnlyList<string>? pruned = null)
        {
            var kept = store.List(mappingId).Where(b => b.Reason == BackupReason.Snapshot).ToList();
            return new SnapshotOutcome(mappingId, status, backupId, bytes, files, pruned ?? [], kept.Sum(b => b.TotalBytes), kept.Count, message);
        }

        var mapping = Shared.TryReadMapping(mappingId);
        if (mapping is null)
        {
            return Outcome(SnapshotStatus.Skipped, $"There is no mapping '{mappingId}'.");
        }

        var target = Shared.TargetPathOf(mapping);
        if (source is null || !IsLink(source) || !PathRules.AreSame(PathRules.ResolveAll(source), PathRules.ResolveAll(target)))
        {
            return Outcome(SnapshotStatus.NotApplied, "Not applied on this device, so its shared folder is not snapshotted here.");
        }

        if (SafeFs.Probe(PathRules.ResolveAll(target)) != EntryKind.Directory)
        {
            return Outcome(SnapshotStatus.Skipped, $"The shared folder '{target}' is missing. Check that the sync root is there.");
        }

        var (files, bytes) = MeasureShared(target);
        var free = SnapshotReader.FreeBytesAt(store.Root);
        if (free is { } available && available < bytes + SnapshotSpaceMargin)
        {
            return Outcome(
                SnapshotStatus.Skipped,
                $"Not enough space where backups are kept: the snapshot needs {bytes:N0} bytes and {available:N0} are free.",
                bytes: bytes, files: files);
        }

        if (dryRun)
        {
            return Outcome(SnapshotStatus.Planned, null, bytes: bytes, files: files);
        }

        try
        {
            var (snapshot, unchanged) = store.CreateSnapshot(mappingId, target);
            var pruned = store.PruneSnapshots(mappingId, keep);
            return Outcome(unchanged ? SnapshotStatus.Unchanged : SnapshotStatus.Taken, null, snapshot.Id, bytes, files, pruned);
        }
        catch (Exception e) when (e is BackupException or IOException or UnauthorizedAccessException)
        {
            return Outcome(SnapshotStatus.Failed, e.Message, bytes: bytes, files: files);
        }
    }

    private static (int Files, long Bytes) MeasureShared(string folder)
    {
        var entries = SafeFs.EnumerateTree(PathRules.ResolveAll(folder)).Where(e => e.Kind == EntryKind.File).ToList();
        return (entries.Count, entries.Sum(e => e.Length));
    }
}
