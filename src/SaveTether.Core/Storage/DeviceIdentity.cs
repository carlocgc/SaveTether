using System.Security.Cryptography;
using SaveTether.Core.Model;

namespace SaveTether.Core.Storage;

/// <summary>
/// Which install owns a device name (design 5.6). The name keys the device's shared config and
/// its seed markers, so two live machines must never use the same one.
/// </summary>
public static class DeviceIdentity
{
    /// <summary>A random ID for this install. It only has to differ between installs.</summary>
    public static string NewInstallId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

    /// <summary>
    /// Gives this install the device name. A new name gets a new device file. An existing name is
    /// refused unless <paramref name="takeOver"/> is set, which is the normal case after a wipe:
    /// the file's paths are kept and its install ID becomes this install's.
    /// </summary>
    /// <param name="backupRoot">Used only when the device file is new.</param>
    public static DeviceFile Claim(SharedStore shared, string deviceName, string backupRoot, bool takeOver)
    {
        ArgumentNullException.ThrowIfNull(shared);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupRoot);

        var existing = shared.TryReadDevice(deviceName);
        if (existing is not null && !takeOver)
        {
            throw new StoreException(
                StoreProblem.NameInUse,
                $"A device named '{deviceName}' is already set up. If this is that device after a reinstall, take the name over. " +
                "If it is a different machine, choose another name: two machines must not share one.");
        }

        var device = existing is null
            ? new DeviceFile
            {
                Schema = DeviceFile.CurrentSchema,
                DeviceName = deviceName,
                InstallId = NewInstallId(),
                BackupRoot = backupRoot,
            }
            : existing with { InstallId = NewInstallId() };

        shared.WriteDevice(device);
        return device;
    }

    /// <summary>
    /// False when the shared device file belongs to another install: either another machine is
    /// using this device's name, or it took the name over.
    /// </summary>
    public static bool Matches(BootstrapFile bootstrap, DeviceFile device)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        ArgumentNullException.ThrowIfNull(device);
        return bootstrap.DeviceName == device.DeviceName && bootstrap.InstallId == device.InstallId;
    }
}
