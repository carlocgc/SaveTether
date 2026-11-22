namespace SaveTether.Core.Operations;

/// <summary>One device in the sync root, as <see cref="Maintenance.Overview"/> reports it.</summary>
/// <param name="LastActivityUtc">The newest of the device file's write time and its seed markers' times.</param>
/// <param name="MappingCount">How many mappings the device has a save folder for.</param>
/// <param name="Tombstoned">This install's ID is in a tombstone: the device was removed, and its file came back.</param>
public sealed record DeviceOverview(
    string Name, string InstallId, DateTimeOffset LastActivityUtc, int MappingCount, bool IsThisDevice, bool Tombstoned);

/// <summary>What is in the sync root and on this device, for "device list" and the app's maintenance page.</summary>
/// <param name="Mappings">Every mapping ID in the shared list.</param>
/// <param name="MappingsHere">The mappings this device has a save folder for.</param>
/// <param name="LinkedHere">The mappings this device has applied, from its own record.</param>
/// <param name="Orphans">Mappings no device has a save folder for.</param>
public sealed record MaintenanceOverview(
    string SyncRoot,
    IReadOnlyList<DeviceOverview> Devices,
    IReadOnlyList<string> Mappings,
    IReadOnlyList<string> MappingsHere,
    IReadOnlyList<string> LinkedHere,
    IReadOnlyList<string> Orphans,
    int BackupCount,
    long BackupBytes);

/// <summary>Device maintenance: looking at the shared setup as a whole (phase 3.6).</summary>
public static class Maintenance
{
    /// <summary>Reads only. Fails like any shared read when the sync root cannot be read.</summary>
    public static MaintenanceOverview Overview(Workspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var shared = workspace.Shared;
        var mappings = shared.ListMappings().Select(m => m.Id).Order(StringComparer.Ordinal).ToList();

        var removed = shared.ReadTombstones().Select(t => t.InstallId).ToHashSet(StringComparer.Ordinal);
        var devices = new List<DeviceOverview>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in shared.ListDeviceNames())
        {
            var device = shared.ReadDevice(name);
            used.UnionWith(device.Paths.Keys);
            devices.Add(new DeviceOverview(
                name, device.InstallId, shared.LastActivityOf(name, mappings), device.Paths.Count,
                name == workspace.DeviceName, removed.Contains(device.InstallId)));
        }

        var backups = workspace.ListBackups();
        return new MaintenanceOverview(
            shared.SyncRoot,
            devices,
            mappings,
            workspace.MappingsWithPaths(),
            workspace.Local.ReadState().Mappings.Keys.Order(StringComparer.Ordinal).ToList(),
            mappings.Where(id => !used.Contains(id)).ToList(),
            backups.Count,
            backups.Sum(b => b.TotalBytes));
    }
}
