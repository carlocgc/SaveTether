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

    /// <summary>Entries still in the shared folder: files that arrived or changed after the backup, which are left alone.</summary>
    public int Left { get; }
}

public sealed partial class Workspace
{
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
    /// device. The shared folder is backed up on this device first and the backup verified. Then exactly the files the backup holds
    /// are deleted (each only if it is still what the backup has, so a save a sync tool delivered meanwhile is never deleted), then the
    /// folders left empty, the app's own folder if it is empty, the seed markers and the mapping file. Anything that arrived or
    /// changed stays, and the record with it: the run says so (<see cref="MappingDeletionIncompleteException"/>) and can be run
    /// again. The caller holds the operation lock and has the owner's confirmation.
    /// </summary>
    public MappingDeletion DeleteMapping(string mappingId)
    {
        var described = DescribeMappingDeletion(mappingId);
        var below = Shared.DataRoot;
        BackupInfo? backup = null;
        var started = false;
        try
        {
            if (described.TargetExists)
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
    /// then every folder that is empty by then, deepest first. Answers how many files (or links) are still there.
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

        // A mapping's shared folder is "app/save folder", so no other mapping's folder can be inside it. Every folder from the
        // shared data folder down must be a real folder: an app folder that is a link would take the deletion somewhere else.
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
