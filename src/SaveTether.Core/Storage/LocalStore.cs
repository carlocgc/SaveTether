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

    /// <summary>Overrides where this device's local files are kept, for every front end.</summary>
    public const string HomeVariable = "SAVETETHER_HOME";

    /// <summary>
    /// This device's own folder, as every front end finds it: <see cref="HomeVariable"/> when set,
    /// otherwise <c>%LOCALAPPDATA%\SaveTether</c> on Windows and <c>$XDG_DATA_HOME/savetether</c>
    /// (by default <c>~/.local/share/savetether</c>) elsewhere.
    /// </summary>
    public static string DefaultFolder()
    {
        if (Environment.GetEnvironmentVariable(HomeVariable) is { Length: > 0 } home)
        {
            return Path.GetFullPath(home);
        }

        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SaveTether");
        }

        var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg && Path.IsPathFullyQualified(xdg)
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(data, "savetether");
    }

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

    /// <summary>
    /// Deletes this device's local files for "device remove": the state, the journal and the lock's holder note, then the
    /// bootstrap last. The bootstrap is the commit point: until it goes the device still counts as set up. The lock file is
    /// tried too, but the caller holds it, and Windows will not delete an open file. <c>gui.log</c> stays.
    /// </summary>
    public void DeleteDeviceFiles(string syncRoot)
    {
        var fs = new SafeFs(syncRoot);
        foreach (var name in new[] { StateName, "journal.json", "operation-holder.json" })
        {
            fs.RemoveFile(Path.Combine(Folder, name));
        }

        try
        {
            fs.RemoveFile(Path.Combine(Folder, "operation.lock"));
        }
        catch (IOException)
        {
            // Held by this process: an empty file, and harmless.
        }

        fs.RemoveFile(Path.Combine(Folder, BootstrapName));
    }
}
