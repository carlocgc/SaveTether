using SaveTether.Core.FileSystem;
using SaveTether.Core.Model;

namespace SaveTether.Core.Storage;

/// <summary>
/// SaveTether's files on this device only, never synced (design 5.6): the bootstrap file that
/// says where the sync root is and which device this is, and the record of what was done here.
/// </summary>
public sealed class LocalStore
{
    private const string BootstrapName = "bootstrap.json";
    private const string StateName = "state.json";

    public LocalStore(string localFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localFolder);
        if (!Path.IsPathFullyQualified(localFolder))
        {
            throw new StoreException(StoreProblem.Invalid, $"The local settings folder must be an absolute path, but got '{localFolder}'.");
        }

        Folder = PathRules.Normalize(localFolder);
    }

    public string Folder { get; }

    /// <summary>Null when this device has not been set up with "init" yet.</summary>
    public BootstrapFile? TryReadBootstrap() =>
        JsonFiles.TryRead(Path.Combine(Folder, BootstrapName), StorageJsonContext.Default.BootstrapFile, BootstrapFile.CurrentSchema);

    public void WriteBootstrap(BootstrapFile bootstrap)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        if (!Identifiers.IsValidDeviceName(bootstrap.DeviceName))
        {
            throw new StoreException(StoreProblem.Invalid, $"'{bootstrap.DeviceName}' is not a valid device name.");
        }

        if (!Path.IsPathFullyQualified(bootstrap.SyncRoot))
        {
            throw new StoreException(StoreProblem.Invalid, $"The sync root must be an absolute path, but got '{bootstrap.SyncRoot}'.");
        }

        Directory.CreateDirectory(Folder);
        JsonFiles.Write(Path.Combine(Folder, BootstrapName), bootstrap, StorageJsonContext.Default.BootstrapFile, BootstrapFile.CurrentSchema);
    }

    /// <summary>The record of what was applied here. Empty when there is none: it can be rebuilt from disk.</summary>
    public StateFile ReadState() =>
        JsonFiles.TryRead(Path.Combine(Folder, StateName), StorageJsonContext.Default.StateFile, StateFile.CurrentSchema)
        ?? StateFile.Empty;

    public void WriteState(StateFile state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Directory.CreateDirectory(Folder);
        JsonFiles.Write(Path.Combine(Folder, StateName), state, StorageJsonContext.Default.StateFile, StateFile.CurrentSchema);
    }
}
