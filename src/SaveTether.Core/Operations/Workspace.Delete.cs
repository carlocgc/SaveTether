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

    /// <summary>Files still in the shared folder, or kept aside for recovery: ones that arrived or changed after the backup, which are never deleted.</summary>
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
    /// device. The shared folder is backed up on this device first and the backup verified. Then, for each file the backup holds, the
    /// file is moved aside in one rename, checked against the backup's size and hash, and deleted only if it matches; a file that
    /// changed in the meantime is put back (or, if a sync tool has already put another file at its path, kept aside for recovery),
    /// and a file that arrived after the backup is not touched. Then the folders left empty, the app's own folder if it is empty,
    /// the seed markers and the mapping file. Anything left stays, and the record with it: the run says so
    /// (<see cref="MappingDeletionIncompleteException"/>) and can be run again, which first settles anything an earlier run left aside.
    /// The caller holds the operation lock and has the owner's confirmation.
    /// </summary>
    public MappingDeletion DeleteMapping(string mappingId)
    {
        var described = DescribeMappingDeletion(mappingId);
        var below = Shared.DataRoot;
        var stageFolder = Path.Combine(Shared.MetaRoot, SharedStore.DeletingFolder, mappingId);
        BackupInfo? backup = null;
        var started = false;
        try
        {
            // What an earlier run set aside and did not finish with is dealt with first, from the backup it names. Settling can put a
            // changed file back, so whether the shared folder is there is looked at after it.
            var carried = SettleInterrupted(stageFolder, described.TargetFolder, below);
            if (carried > 0)
            {
                started = true;
                throw new MappingDeletionIncompleteException(
                    $"'{mappingId}' was deleted only in part: {carried} file(s) an earlier run set aside could not be put back or deleted, " +
                    $"and are kept in '{stageFolder}'. Look at them, then run 'map delete {mappingId}' again.",
                    null, carried);
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
                var operation = Path.Combine(stageFolder, DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", System.Globalization.CultureInfo.InvariantCulture));
                var (remaining, kept) = RemoveBackedUp(described.TargetFolder, manifest, below, operation, backup?.Id);
                if (remaining + kept > 0)
                {
                    throw new MappingDeletionIncompleteException(
                        $"'{mappingId}' was deleted only in part: {remaining + kept} file(s) arrived in its shared folder or changed after the backup was taken, " +
                        "so they were left, and so was its record (a sync tool may still be delivering). " +
                        (kept > 0 ? $"{kept} changed file(s) are kept in '{operation}' because the sync tool had already put another file in their place. " : string.Empty) +
                        (backup is null ? string.Empty : $"What was removed is backed up as {backup.Id}. ") +
                        $"Let the sync tool finish, then run 'map delete {mappingId}' again.",
                        backup, remaining + kept);
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
    /// Deletes the files a backup holds from the shared folder, one at a time: the file is moved to <paramref name="operation"/> (a
    /// folder of the tool's own, below the share's metadata, so on the same filesystem and a single rename), then checked against
    /// the backup's size and hash and deleted only if it matches. A save a sync tool writes at any moment is therefore either
    /// moved with the file (and put back, since it no longer matches) or lands afterwards at the original path (and is left).
    /// Then every folder that is empty by then, deepest first. Answers the files still in the shared folder, and the changed
    /// copies kept aside for recovery.
    /// </summary>
    private (int Remaining, int Kept) RemoveBackedUp(string target, BackupManifest? manifest, string below, string operation, string? backupId)
    {
        var kept = 0;
        foreach (var file in manifest?.Files ?? [])
        {
            var path = Path.Combine([target, .. file.Path.Split('/')]);
            if (SafeFs.Probe(path) != EntryKind.File)
            {
                continue;
            }

            // The name of the backup goes first, so an interrupted run can be settled from it.
            if (SafeFs.Probe(Path.Combine(operation, "backup")) != EntryKind.File)
            {
                Directory.CreateDirectory(operation);
                SafeFs.WriteFileAtomically(Path.Combine(operation, "backup"), backupId ?? string.Empty);
            }

            var staged = Path.Combine([operation, .. file.Path.Split('/')]);
            DeletionSeam?.Invoke("before-stage:" + file.Path);
            SafeFs.MoveSharedFile(below, path, staged);
            DeletionSeam?.Invoke("after-stage:" + file.Path);
            if (!SettleStaged(staged, path, file, below))
            {
                kept++;
            }
        }

        if (kept == 0)
        {
            ClearStaging(operation, below);
        }

        var remaining = 0;
        if (SafeFs.Probe(target) == EntryKind.Directory)
        {
            var folders = SafeFs.EnumerateTree(target)
                .Where(e => e.Kind == EntryKind.Directory)
                .Select(e => Path.Combine([target, .. e.RelativePath.Split('/')]))
                .OrderByDescending(p => p.Length)
                .ToList();
            foreach (var folder in folders)
            {
                SafeFs.RemoveEmptySharedFolder(below, folder);
            }

            if (!SafeFs.RemoveEmptySharedFolder(below, target))
            {
                remaining = SafeFs.EnumerateTree(target).Count(e => e.Kind != EntryKind.Directory);
            }
        }

        return (remaining, kept);
    }

    /// <summary>
    /// A file set aside: deleted when it is what the backup holds; otherwise put back where it was, if nothing is there now. When a sync
    /// tool has already put another file at its path, it is left alone and the set-aside copy is kept. False when it is kept.
    /// </summary>
    private static bool SettleStaged(string staged, string path, BackupFile? entry, string below)
    {
        if (entry is not null
            && new FileInfo(staged).Length == entry.Size
            && string.Equals(FileHasher.Sha256Hex(staged), entry.Sha256, StringComparison.Ordinal))
        {
            SafeFs.RemoveSharedFile(below, staged);
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
    /// Settles what an earlier <c>map delete</c> left set aside, from the backup it recorded. Answers the files that could not be
    /// settled (a sync tool put another file at their path), which stay where they are.
    /// </summary>
    private int SettleInterrupted(string stageFolder, string target, string below)
    {
        if (SafeFs.Probe(stageFolder) != EntryKind.Directory)
        {
            return 0;
        }

        var kept = 0;
        foreach (var operation in Directory.EnumerateDirectories(stageFolder))
        {
            var backupFile = Path.Combine(operation, "backup");
            var backupId = SafeFs.Probe(backupFile) == EntryKind.File ? File.ReadAllText(backupFile).Trim() : string.Empty;
            var manifest = backupId.Length == 0 ? null : OpenBackups(ReadOwnDevice()).ReadManifest(backupId);
            foreach (var entry in SafeFs.EnumerateTree(operation).Where(e => e.Kind == EntryKind.File && e.RelativePath != "backup").ToList())
            {
                var staged = Path.Combine([operation, .. entry.RelativePath.Split('/')]);
                var path = Path.Combine([target, .. entry.RelativePath.Split('/')]);
                if (!SettleStaged(staged, path, manifest?.Files.FirstOrDefault(f => f.Path == entry.RelativePath), below))
                {
                    kept++;
                }
            }

            if (kept == 0)
            {
                ClearStaging(operation, below);
            }
        }

        if (kept == 0)
        {
            SafeFs.RemoveEmptySharedFolder(below, stageFolder);
        }

        return kept;
    }

    /// <summary>Removes a staging folder that holds nothing but its backup note and empty folders. A folder that still holds a file stays.</summary>
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
            SafeFs.RemoveEmptySharedFolder(below, folder);
        }

        var note = Path.Combine(operation, "backup");
        if (SafeFs.EnumerateTree(operation).All(e => e.RelativePath == "backup"))
        {
            SafeFs.RemoveSharedFile(below, note);
        }

        SafeFs.RemoveEmptySharedFolder(below, operation);

        // The mapping's own staging folder goes too once no operation is left in it.
        if (Path.GetDirectoryName(operation) is { } mappingFolder)
        {
            SafeFs.RemoveEmptySharedFolder(below, mappingFolder);
        }
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
