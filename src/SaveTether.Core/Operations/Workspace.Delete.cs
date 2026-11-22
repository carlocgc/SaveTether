using SaveTether.Core.Backup;
using SaveTether.Core.FileSystem;
using SaveTether.Core.Model;
using SaveTether.Core.Storage;

namespace SaveTether.Core.Operations;

/// <summary>What deleting an unused shared mapping would remove (or did): the shared folder with its size, and the record.</summary>
/// <param name="TargetFolder">The shared folder that holds the saves.</param>
/// <param name="TargetExists">False when the folder is already gone, which a run that stopped part way leaves.</param>
/// <param name="Files">Files in the shared folder.</param>
/// <param name="Bytes">Their total size.</param>
/// <param name="Backup">Set once the deletion ran: the verified backup taken first, or null when the folder held no files.</param>
public sealed record MappingDeletion(string MappingId, string TargetFolder, bool TargetExists, int Files, long Bytes, BackupInfo? Backup = null);

/// <summary>
/// A <c>map delete</c> that stopped after it had started removing the shared copy, so part of it is gone: the files the backup
/// covers, perhaps all of them, but not the record. Safe to run again, which finishes what is left. <see cref="Backup"/> is the
/// verified backup of what was removed, when the folder held files.
/// </summary>
public sealed class MappingDeletionIncompleteException : IOException
{
    public MappingDeletionIncompleteException()
    {
    }

    public MappingDeletionIncompleteException(string message)
        : base(message)
    {
    }

    public MappingDeletionIncompleteException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public MappingDeletionIncompleteException(string message, BackupInfo? backup, int left, Exception? innerException = null)
        : base(message, innerException)
    {
        Backup = backup;
        Left = left;
    }

    /// <summary>The verified backup of the shared copy taken before anything was removed; null when there were no files.</summary>
    public BackupInfo? Backup { get; }

    /// <summary>Files still in the shared folder: ones that arrived or changed after the backup, which are never deleted.</summary>
    public int Left { get; }
}

public sealed partial class Workspace
{
    private const string StagingNote = "backup.json";
    private const string StagingFiles = "files";

    /// <summary>Test seam: called with the stage the deletion is at, so a test can change the share the way a sync tool would.</summary>
    internal Action<string>? DeletionSeam { get; set; }

    /// <summary>
    /// What <see cref="DeleteMapping"/> would remove, after the same checks. Reads only. Refuses (a <see cref="StoreException"/>,
    /// nothing changed) when the mapping is still in use or cannot be told to be unused.
    /// </summary>
    public MappingDeletion DescribeMappingDeletion(string mappingId)
    {
        var target = CheckMappingUnused(mappingId);
        if (SafeFs.Probe(target) == EntryKind.Missing)
        {
            return new MappingDeletion(mappingId, target, TargetExists: false, Files: 0, Bytes: 0);
        }

        var files = SafeFs.EnumerateTree(target).Where(e => e.Kind == EntryKind.File).ToList();
        return new MappingDeletion(mappingId, target, TargetExists: true, files.Count, files.Sum(f => f.Length));
    }

    /// <summary>
    /// "map delete" (design 5.3, invariant 2's third exception): removes a mapping no device uses, from the shared folder, for every
    /// device. The owner confirms that every write to the role's shared folder is complete and the sync tool has finished with it on
    /// every device; nothing here can know that. The shared folder is backed up on this device first and the backup verified. Then each
    /// file the backup holds is deleted if it is still what the backup has (same size and hash): a file that arrived or changed after
    /// the backup is not touched. Then the folders left empty (never recursively), the app's own folder if it is empty, the seed
    /// markers and the mapping file. Anything left stays, and the record with it: the run says so
    /// (<see cref="MappingDeletionIncompleteException"/>) and can be run again, which first settles anything an earlier build left set
    /// aside. The caller holds the operation lock and has the owner's confirmation.
    /// </summary>
    public MappingDeletion DeleteMapping(string mappingId)
    {
        var described = DescribeMappingDeletion(mappingId);
        var below = Shared.DataRoot;
        BackupInfo? backup = null;
        var started = false;
        try
        {
            // Files an earlier build set aside and did not finish with are dealt with first, from the backup they name.
            var carried = SettleInterrupted(mappingId, described.TargetFolder, below);
            if (carried.Kept > 0)
            {
                started = true;
                throw new MappingDeletionIncompleteException(
                    $"'{mappingId}' was deleted only in part: {carried.Kept} file(s) an earlier run set aside could not be put back or deleted, " +
                    $"and are kept in '{carried.Where}'. Look at them, then run 'map delete {mappingId}' again.",
                    null, carried.Kept);
            }

            if (SafeFs.Probe(described.TargetFolder) == EntryKind.Directory)
            {
                // The files are listed now, not from the description: a sync tool may have delivered some since.
                DeletionSeam?.Invoke("before-backup");
                if (SafeFs.EnumerateTree(described.TargetFolder).Any(e => e.Kind == EntryKind.File))
                {
                    backup = OpenBackups(ReadOwnDevice()).Create(mappingId, described.TargetFolder, BackupReason.PreDelete);
                }

                DeletionSeam?.Invoke("after-backup");
                var manifest = backup is null ? null : OpenBackups(ReadOwnDevice()).ReadManifest(backup.Id);
                if (backup is not null && manifest is null)
                {
                    throw new BackupException($"The backup '{backup.Id}' of the shared folder cannot be read, so nothing is deleted.");
                }

                started = true;
                var left = RemoveBackedUp(described.TargetFolder, manifest, below);
                if (left > 0)
                {
                    throw new MappingDeletionIncompleteException(
                        $"'{mappingId}' was deleted only in part: {left} file(s) arrived in its shared folder or changed after the backup was taken, " +
                        "so they were left, and so was its record (a sync tool may still be delivering). " +
                        (backup is null ? string.Empty : $"What was removed is backed up as {backup.Id}. ") +
                        $"Let the sync tool finish, then run 'map delete {mappingId}' again.",
                        backup, left);
                }

                // The app's own folder under savetether/ goes with it, only if it is empty by now; one that gained anything stays.
                DeletionSeam?.Invoke("before-app-folder");
                var parent = Path.GetDirectoryName(described.TargetFolder);
                if (parent is not null && !PathRules.AreSame(parent, below))
                {
                    SafeFs.RemoveEmptySharedFolder(below, parent);
                }
            }

            DeletionSeam?.Invoke("before-retire");
            started = true;
            Shared.RetireMapping(mappingId);
        }
        catch (Exception e) when (started && e is (IOException or UnauthorizedAccessException) and not MappingDeletionIncompleteException)
        {
            throw new MappingDeletionIncompleteException(
                $"'{mappingId}' was deleted only in part: its shared saves may already be removed, and the deletion stopped before it finished: {e.Message} " +
                (backup is null ? string.Empty : $"What was removed is backed up as {backup.Id}. ") +
                $"Run 'map delete {mappingId}' again to finish, once what stopped it is dealt with.",
                backup, 0, e);
        }

        return described with { Backup = backup };
    }

    /// <summary>
    /// Deletes the files a backup holds from the shared folder, each only while it is still what the backup has (same size and hash),
    /// then every folder that is empty by then, deepest first, never recursively. Answers how many files (or links) are still there.
    /// </summary>
    private static int RemoveBackedUp(string target, BackupManifest? manifest, string below)
    {
        foreach (var file in manifest?.Files ?? [])
        {
            var path = Path.Combine([target, .. file.Path.Split('/')]);
            if (SafeFs.Probe(path) == EntryKind.File
                && new FileInfo(path).Length == file.Size
                && string.Equals(FileHasher.Sha256Hex(path), file.Sha256, StringComparison.Ordinal))
            {
                SafeFs.RemoveSharedFile(below, path);
            }
        }

        if (SafeFs.Probe(target) != EntryKind.Directory)
        {
            return 0;
        }

        var folders = SafeFs.EnumerateTree(target)
            .Where(e => e.Kind == EntryKind.Directory)
            .Select(e => Path.Combine([target, .. e.RelativePath.Split('/')]))
            .OrderByDescending(p => p.Length)
            .ToList();
        foreach (var folder in folders)
        {
            SafeFs.RemoveEmptySharedFolder(below, folder);
        }

        return SafeFs.RemoveEmptySharedFolder(below, target) ? 0 : SafeFs.EnumerateTree(target).Count(e => e.Kind != EntryKind.Directory);
    }

    /// <summary>
    /// A file an earlier build set aside: deleted when it is what the backup holds; otherwise put back where it was, if nothing is there
    /// now. When something is there, it is left alone and the set-aside copy is kept. False when it is kept.
    /// </summary>
    private static bool SettleStaged(string staged, string path, BackupFile? entry, string below)
    {
        if (entry is not null
            && new FileInfo(staged).Length == entry.Size
            && string.Equals(FileHasher.Sha256Hex(staged), entry.Sha256, StringComparison.Ordinal))
        {
            SafeFs.RemoveStagedFile(below, staged);
            return true;
        }

        if (SafeFs.Probe(path) == EntryKind.Missing)
        {
            SafeFs.MoveSharedFile(below, staged, path);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Settles what an earlier build of <c>map delete</c> left set aside (it set files aside, in a folder beside the sync folder or below
    /// the share's metadata, before deleting them), from the backup it recorded. Answers the files that could not be settled, which stay
    /// where they are, and where. Nothing is set aside any more, so with nothing there this does nothing.
    /// </summary>
    private (int Kept, string Where) SettleInterrupted(string mappingId, string target, string below)
    {
        var kept = 0;
        var where = string.Empty;
        foreach (var stageFolder in LegacyStagingFolders(mappingId))
        {
            if (SafeFs.Probe(stageFolder) != EntryKind.Directory)
            {
                continue;
            }

            foreach (var operation in Directory.EnumerateDirectories(stageFolder))
            {
                var note = Path.Combine(operation, StagingNote);
                var backupId = SafeFs.Probe(note) == EntryKind.File ? File.ReadAllText(note).Trim() : string.Empty;
                var manifest = backupId.Length == 0 ? null : OpenBackups(ReadOwnDevice()).ReadManifest(backupId);
                var files = Path.Combine(operation, StagingFiles);
                var left = 0;
                if (SafeFs.Probe(files) == EntryKind.Directory)
                {
                    foreach (var entry in SafeFs.EnumerateTree(files).Where(e => e.Kind == EntryKind.File).ToList())
                    {
                        var staged = Path.Combine([files, .. entry.RelativePath.Split('/')]);
                        var path = Path.Combine([target, .. entry.RelativePath.Split('/')]);
                        if (!SettleStaged(staged, path, manifest?.Files.FirstOrDefault(f => f.Path == entry.RelativePath), below))
                        {
                            left++;
                        }
                    }
                }

                if (left == 0)
                {
                    ClearStaging(operation, below);
                }
                else
                {
                    kept += left;
                    where = operation;
                }
            }

            if (kept == 0)
            {
                SafeFs.RemoveEmptyStagedFolder(below, stageFolder);
            }
        }

        return (kept, where);
    }

    /// <summary>Where an earlier build set files aside: beside the sync folder, and below the share's metadata.</summary>
    private IEnumerable<string> LegacyStagingFolders(string mappingId)
    {
        var syncRoot = PathRules.Normalize(Shared.SyncRoot);
        if (Path.GetDirectoryName(syncRoot) is { } beside)
        {
            yield return Path.Combine(beside, ".savetether-deleting", Path.GetFileName(syncRoot), mappingId);
        }

        yield return Path.Combine(Shared.MetaRoot, SharedStore.DeletingFolder, mappingId);
    }

    /// <summary>Removes a staging folder that holds nothing but its note and empty folders. A folder that still holds a file stays.</summary>
    private static void ClearStaging(string operation, string below)
    {
        if (SafeFs.Probe(operation) != EntryKind.Directory)
        {
            return;
        }

        var folders = SafeFs.EnumerateTree(operation)
            .Where(e => e.Kind == EntryKind.Directory)
            .Select(e => Path.Combine([operation, .. e.RelativePath.Split('/')]))
            .OrderByDescending(p => p.Length)
            .ToList();
        foreach (var folder in folders)
        {
            SafeFs.RemoveEmptyStagedFolder(below, folder);
        }

        if (SafeFs.EnumerateTree(operation).All(e => e.RelativePath == StagingNote))
        {
            SafeFs.RemoveStagedFile(below, Path.Combine(operation, StagingNote));
        }

        SafeFs.RemoveEmptyStagedFolder(below, operation);

        // The mapping's own staging folder goes too once no operation is left in it, and the folders above it beside the sync folder.
        if (Path.GetDirectoryName(operation) is { } mappingFolder && SafeFs.RemoveEmptyStagedFolder(below, mappingFolder)
            && Path.GetDirectoryName(mappingFolder) is { } syncName && Path.GetFileName(Path.GetDirectoryName(syncName)) == ".savetether-deleting"
            && SafeFs.RemoveEmptyStagedFolder(below, syncName) && Path.GetDirectoryName(syncName) is { } top)
        {
            SafeFs.RemoveEmptyStagedFolder(below, top);
        }
    }

    /// <summary>
    /// Deletes only a mapping's record (phase 3.6, the Maintenance screen's "Delete mapping"): its shared save folder stays, as when
    /// "device remove" is told to delete just the mapping. Refused (a <see cref="StoreException"/>) while a device uses it.
    /// The caller holds the operation lock and has the owner's confirmation.
    /// </summary>
    public void DeleteMappingRecord(string mappingId)
    {
        CheckMappingUnused(mappingId);
        Shared.RetireMapping(mappingId);
    }

    /// <summary>Checks nothing uses the mapping and answers its shared folder. Throws a <see cref="StoreException"/> when something does.</summary>
    private string CheckMappingUnused(string mappingId)
    {
        if (Journal.IsOpen)
        {
            throw new StoreException(
                StoreProblem.Conflict,
                new Note("An earlier operation did not finish. Recover it first.", new NextStep(NextStepKind.Recover)));
        }

        var mapping = Shared.TryReadMapping(mappingId)
            ?? throw new StoreException(StoreProblem.NotFound, $"There is no shared mapping '{mappingId}'.");

        // Every device's file, this one's too: a device that has a folder set for it may be linked to the shared folder.
        // A file that cannot be read stops this, since that device cannot be told not to use it.
        var usedBy = new List<string>();
        foreach (var name in Shared.ListDeviceNames())
        {
            if (Shared.ReadDevice(name).Paths.ContainsKey(mappingId))
            {
                usedBy.Add(name);
            }
        }

        if (usedBy.Count > 0 || Local.ReadState().Mappings.ContainsKey(mappingId))
        {
            var where = usedBy.Count > 0 ? string.Join(", ", usedBy) : DeviceName;
            // No next step: the CLI words RemoveMapping for another case ("use it, or remove it"), and the message already says what to do.
            throw new StoreException(
                new Note($"'{mappingId}' is still set up on {where}. Remove it there first ('map remove', after 'revert' if it is applied)."),
                new ConflictFacts(ConflictKind.InUseOnDevices, mappingId));
        }

        // A mapping's shared folder is "app/save folder", so no other mapping's folder can be inside it. The shared data folder
        // itself and every folder below it must be a real folder: a link in either place would take the deletion somewhere else.
        var target = Shared.TargetPathOf(mapping);
        try
        {
            SafeFs.RequirePlainFolders(Shared.DataRoot, target);
        }
        catch (SafeFsException e)
        {
            throw new StoreException(StoreProblem.Invalid, $"The shared folder of '{mappingId}' is reached through a link, so it is not deleted. Look at it by hand. {e.Message}");
        }

        if (SafeFs.Probe(target) is not (EntryKind.Missing or EntryKind.Directory))
        {
            throw new StoreException(StoreProblem.Invalid, $"The shared folder '{target}' is not a plain folder, so it is not deleted. Look at it by hand.");
        }

        return target;
    }
}
