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

public sealed partial class Workspace
{
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
    /// device. The shared folder is backed up first on this device and the backup verified; then the folder, the seed markers
    /// and the mapping file are deleted, in that order, so a run that stopped part way can be run again. The caller holds the
    /// operation lock and has the owner's confirmation.
    /// </summary>
    public MappingDeletion DeleteMapping(string mappingId)
    {
        var described = DescribeMappingDeletion(mappingId);
        var fs = new SafeFs(Shared.SyncRoot);
        BackupInfo? backup = null;
        if (described.TargetExists)
        {
            if (described.Files > 0)
            {
                backup = OpenBackups(ReadOwnDevice()).Create(mappingId, described.TargetFolder, BackupReason.PreDelete);
            }

            fs.DeleteTree(described.TargetFolder, SharedDataDeletion.ExplicitlyConfirmed);

            // The app's own folder under savetether/ goes too once nothing else is in it.
            var parent = Path.GetDirectoryName(described.TargetFolder);
            if (parent is not null
                && !PathRules.AreSame(parent, Shared.DataRoot)
                && PathRules.IsSameOrInside(parent, Shared.DataRoot)
                && SafeFs.Probe(parent) == EntryKind.Directory
                && !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                fs.DeleteTree(parent, SharedDataDeletion.ExplicitlyConfirmed);
            }
        }

        Shared.RetireMapping(mappingId, fs);
        return described with { Backup = backup };
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

        // A mapping's shared folder is "app/save folder", so no other mapping's folder can be inside it.
        var target = Shared.TargetPathOf(mapping);
        if (SafeFs.Probe(target) is not (EntryKind.Missing or EntryKind.Directory))
        {
            throw new StoreException(StoreProblem.Invalid, $"The shared folder '{target}' is not a plain folder, so it is not deleted. Look at it by hand.");
        }

        return target;
    }
}
