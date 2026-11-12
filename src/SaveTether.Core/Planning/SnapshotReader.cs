using SaveTether.Core.Backup;
using SaveTether.Core.FileSystem;
using SaveTether.Core.Model;
using SaveTether.Core.Storage;

namespace SaveTether.Core.Planning;

/// <summary>
/// Looks at the real system and fills in a <see cref="Snapshot"/> for the planner. It only
/// reads: nothing here creates, changes or deletes anything.
/// </summary>
public static class SnapshotReader
{
    public static Snapshot Read(
        SharedStore shared, MappingFile mapping, string deviceName, DevicePath devicePath, string backupRoot,
        IProcessLister processes, bool journalOpen)
    {
        ArgumentNullException.ThrowIfNull(shared);
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(devicePath);
        ArgumentNullException.ThrowIfNull(processes);

        if (!Path.IsPathFullyQualified(devicePath.Source))
        {
            throw new StoreException(
                StoreProblem.Invalid,
                $"The save folder for '{mapping.Id}' on this device must be an absolute path, but is '{devicePath.Source}'.");
        }

        var source = PathRules.Normalize(devicePath.Source);
        var target = shared.TargetPathOf(mapping);
        var initialized = shared.IsInitialized;

        var sourceKind = SafeFs.Probe(source);
        var targetKind = SafeFs.Probe(target);
        var sourceParent = Path.GetDirectoryName(source);

        var sourceTree = sourceKind == EntryKind.Directory ? ReadTree(source) : TreeFacts.Empty;
        var targetTree = targetKind == EntryKind.Directory ? ReadTree(target) : TreeFacts.Empty;
        (sourceTree, targetTree) = HashWhereComparable(source, sourceTree, target, targetTree);

        var markers = initialized
            ? shared.ReadSeedMarkers(mapping.Id).Where(m => m.IsActive).ToList()
            : [];

        return new Snapshot
        {
            MappingId = mapping.Id,
            DeviceName = deviceName,
            Source = source,
            Target = target,
            ResolvedSource = PathRules.ResolveParents(source),
            ResolvedTarget = PathRules.ResolveAll(target),
            SyncRoot = PathRules.ResolveAll(shared.SyncRoot),
            DataRoot = PathRules.ResolveAll(shared.DataRoot),
            SyncRootInitialized = initialized,
            SourceKind = sourceKind,
            SourceLinkTarget = sourceKind is EntryKind.Symlink or EntryKind.Junction
                ? PathRules.ResolveAll(SafeFs.ReadLinkTarget(source))
                : null,
            SourceParentExists = sourceParent is not null && SafeFs.Probe(PathRules.ResolveAll(sourceParent)) == EntryKind.Directory,
            SourceTree = sourceTree,
            TargetKind = targetKind,
            TargetTree = targetTree,
            ActiveMarkers = markers,
            MissingSeedFiles = MissingSeedFiles(markers, targetTree),
            ProcessNamesChecked = devicePath.ProcessNames,
            RunningProcesses = devicePath.ProcessNames.Count == 0
                ? []
                : ProcessCheck.FindRunning(devicePath.ProcessNames, processes.RunningProcessNames()),
            JournalOpen = journalOpen,
            BackupRoot = backupRoot,
            BackupRootProblem = BackupRootProblem(backupRoot, shared.SyncRoot),
            FreeBytesAtBackupRoot = FreeBytesAt(backupRoot),
            FreeBytesAtTarget = FreeBytesAt(shared.SyncRoot),
            TargetOnLocalVolume = IsOnLocalVolume(target),
        };
    }

    private static TreeFacts ReadTree(string root) => new(
        SafeFs.EnumerateTree(root)
            .Select(e => new FileFact(e.RelativePath, e.Kind, e.Length, e.LastWriteTimeUtc))
            .ToList());

    /// <summary>
    /// Hashes only the files that could be identical: same path, both plain files, same size.
    /// Everything else is already known to differ, and save folders can be gigabytes.
    /// </summary>
    private static (TreeFacts Source, TreeFacts Target) HashWhereComparable(
        string sourceRoot, TreeFacts source, string targetRoot, TreeFacts target)
    {
        var targetByPath = target.Entries.ToDictionary(e => e.RelativePath, StringComparer.Ordinal);
        var comparable = source.Entries
            .Where(e => e.Kind == EntryKind.File
                && targetByPath.TryGetValue(e.RelativePath, out var other)
                && other.Kind == EntryKind.File
                && other.Length == e.Length)
            .Select(e => e.RelativePath)
            .ToHashSet(StringComparer.Ordinal);

        if (comparable.Count == 0)
        {
            return (source, target);
        }

        return (WithHashes(sourceRoot, source, comparable), WithHashes(targetRoot, target, comparable));
    }

    private static TreeFacts WithHashes(string root, TreeFacts tree, HashSet<string> paths) => new(
        tree.Entries
            .Select(e => paths.Contains(e.RelativePath)
                ? e with { Sha256 = FileHasher.Sha256Hex(Path.Combine(root, e.RelativePath)) }
                : e)
            .ToList());

    private static List<string> MissingSeedFiles(IReadOnlyList<SeedMarker> markers, TreeFacts target)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var present = target.Entries.Where(e => e.Kind == EntryKind.File).Select(e => e.RelativePath).ToHashSet(comparer);
        return markers
            .SelectMany(m => m.Files)
            .Select(f => f.Path)
            .Distinct(comparer)
            .Where(path => !present.Contains(path))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static string? BackupRootProblem(string backupRoot, string syncRoot)
    {
        try
        {
            _ = new BackupStore(backupRoot, syncRoot);
            return null;
        }
        catch (Exception e) when (e is BackupException or ArgumentException)
        {
            return e.Message;
        }
    }

    private static long? FreeBytesAt(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path))
            {
                return null;
            }

            // Walk up to something that exists; free space is a property of the volume.
            var existing = PathRules.Normalize(path);
            while (SafeFs.Probe(existing) == EntryKind.Missing && Path.GetDirectoryName(existing) is { } parent)
            {
                existing = parent;
            }

            return new DriveInfo(existing).AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsOnLocalVolume(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            return Path.GetPathRoot(path) is { Length: > 0 } root && new DriveInfo(root).DriveType != DriveType.Network;
        }
        catch (Exception e) when (e is IOException or ArgumentException)
        {
            return true;
        }
    }
}
