using SaveTether.Core.Model;
using SaveTether.Core.Storage;

namespace SaveTether.Core.Operations;

/// <summary>"device rename": the name this device had, and the one it has now. They are equal when only leftovers were cleared.</summary>
public sealed record RenameResult(string OldName, string NewName);

public sealed partial class Workspace
{
    /// <summary>
    /// Gives this device a new name (phase 3.6, T3). The name keys the device file, the seed markers and the bootstrap, so:
    /// the new files are written first, the bootstrap is the commit point, and the old files go last. Each step can be run
    /// again: a crash leaves two device files with this install's ID, and a second rename finishes the job. Other devices'
    /// markers that name the old device as <c>SupersededBy</c> are left, as each device writes only its own files.
    /// </summary>
    public RenameResult RenameDevice(string newName)
    {
        if (!Identifiers.IsValidDeviceName(newName))
        {
            throw new StoreException(
                StoreProblem.Invalid,
                $"'{newName}' is not a valid device name. Use lowercase letters, digits, '-' and '_', for example 'deck'.");
        }

        if (Journal.IsOpen)
        {
            throw new StoreException(
                StoreProblem.Conflict,
                new Note("An earlier operation did not finish. Recover it first.", new NextStep(NextStepKind.Recover)));
        }

        var oldName = DeviceName;
        var installId = Bootstrap.InstallId;
        var names = Shared.ListDeviceNames(); // Throws when the sync root cannot be read.

        // A device file with the new name is ours only when it carries this install's ID: an unfinished rename.
        var target = Shared.TryReadDevice(newName);
        if (target is not null && target.InstallId != installId)
        {
            throw new StoreException(
                StoreProblem.NameInUse,
                $"A device named '{newName}' is already set up. Choose another name: two machines must not share one.");
        }

        var current = Shared.TryReadDevice(oldName);
        if (current is not null && current.InstallId != installId)
        {
            throw new StoreException(
                StoreProblem.Conflict,
                $"The device file '{oldName}' belongs to another install, so this device cannot be renamed from it. Run 'status' for what to do.");
        }

        var source = current ?? target
            ?? throw new StoreException(StoreProblem.NotFound, $"This device's file '{oldName}' is not in the sync folder, so it cannot be renamed.");

        var mappingIds = Shared.ListMappings().Select(m => m.Id).ToList();
        var markers = new List<SeedMarker>();
        if (newName != oldName)
        {
            foreach (var id in mappingIds)
            {
                markers.AddRange(Shared.ReadSeedMarkers(id).Where(m => m.DeviceName == oldName));
            }

            Shared.WriteDevice(source with { DeviceName = newName });
            foreach (var marker in markers)
            {
                Shared.WriteSeedMarker(marker with { DeviceName = newName });
            }

            Local.WriteBootstrap(Bootstrap with { DeviceName = newName }); // The commit point.
        }

        // Every other file of this install's: the old name's, and any an earlier rename left.
        foreach (var name in names.Where(n => n != newName))
        {
            // A file that cannot be read is not one this install wrote, and must not stop the cleanup after the commit.
            DeviceFile? leftover;
            try
            {
                leftover = Shared.TryReadDevice(name);
            }
            catch (StoreException)
            {
                continue;
            }

            if (leftover is null || leftover.InstallId != installId)
            {
                continue;
            }

            foreach (var id in mappingIds)
            {
                Shared.DeleteSeedMarker(id, name);
            }

            Shared.DeleteDevice(name);
        }

        return new RenameResult(oldName, newName);
    }
}
