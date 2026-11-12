using SaveTether.Core.FileSystem;
using SaveTether.Core.Model;

namespace SaveTether.Core.Storage;

/// <summary>
/// SaveTether's files inside the sync root (design 5.6). Everything lives under
/// &lt;sync root&gt;/savetether/, with metadata in its .savetether/ folder. Each file belongs to one
/// mapping or one device, so two devices never write the same file.
/// </summary>
public sealed class SharedStore
{
    public const string ToolFolderName = "savetether";
    public const string MetaFolderName = ".savetether";

    private const string MappingsFolder = "mappings";
    private const string DevicesFolder = "devices";
    private const string SeedFolder = "seed";

    public SharedStore(string syncRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(syncRoot);
        if (!Path.IsPathFullyQualified(syncRoot))
        {
            throw new StoreException(StoreProblem.Invalid, $"The sync root must be an absolute path, but got '{syncRoot}'.");
        }

        SyncRoot = PathRules.Normalize(syncRoot);
        DataRoot = Path.Combine(SyncRoot, ToolFolderName);
        MetaRoot = Path.Combine(DataRoot, MetaFolderName);
    }

    public string SyncRoot { get; }

    /// <summary>&lt;sync root&gt;/savetether: the only folder in the sync root SaveTether uses.</summary>
    public string DataRoot { get; }

    /// <summary>&lt;sync root&gt;/savetether/.savetether: the marker folder, holding all shared metadata.</summary>
    public string MetaRoot { get; }

    /// <summary>
    /// True when the marker folder is present. Without it nothing may run (design 5.3, invariant 5):
    /// an unmounted drive or a sync that has not arrived yet both look like "not initialized".
    /// </summary>
    public bool IsInitialized => SafeFs.Probe(MetaRoot) == EntryKind.Directory;

    /// <summary>
    /// Creates the marker folder. Only for the first device ever ("init --create-root"); every
    /// other device must receive it through the sync tool.
    /// </summary>
    public void CreateRoot()
    {
        if (SafeFs.Probe(PathRules.ResolveAll(SyncRoot)) != EntryKind.Directory)
        {
            throw new StoreException(
                StoreProblem.NotFound,
                $"The sync root '{SyncRoot}' does not exist. Create it, or check that its drive is connected.");
        }

        Directory.CreateDirectory(Path.Combine(MetaRoot, MappingsFolder));
        Directory.CreateDirectory(Path.Combine(MetaRoot, DevicesFolder));
        Directory.CreateDirectory(Path.Combine(MetaRoot, SeedFolder));
    }

    /// <summary>The absolute folder a mapping's saves are shared in.</summary>
    public string TargetPathOf(MappingFile mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        return Path.Combine([DataRoot, .. mapping.Target.Split('/')]);
    }

    public MappingFile ReadMapping(string mappingId)
    {
        RequireInitialized();
        RequireMappingId(mappingId);
        return CheckMapping(
            JsonFiles.Read(MappingPath(mappingId), StorageJsonContext.Default.MappingFile, MappingFile.CurrentSchema),
            mappingId);
    }

    public MappingFile? TryReadMapping(string mappingId)
    {
        RequireInitialized();
        RequireMappingId(mappingId);
        return SafeFs.Probe(MappingPath(mappingId)) == EntryKind.File ? ReadMapping(mappingId) : null;
    }

    /// <summary>Every mapping, by ID. A single unreadable file fails the listing, naming the file.</summary>
    public IReadOnlyList<MappingFile> ListMappings()
    {
        RequireInitialized();
        return JsonFiles.ListJson(Path.Combine(MetaRoot, MappingsFolder))
            .Select(file => ReadMapping(Path.GetFileNameWithoutExtension(file)))
            .ToList();
    }

    /// <summary>
    /// Adds a mapping, or rewrites one unchanged. An existing mapping's target cannot be changed,
    /// because other devices already link to it.
    /// </summary>
    public void WriteMapping(MappingFile mapping)
    {
        RequireInitialized();
        ArgumentNullException.ThrowIfNull(mapping);
        RequireMappingId(mapping.Id);
        CheckMapping(mapping, mapping.Id);

        foreach (var other in ListMappings())
        {
            if (other.Id == mapping.Id)
            {
                if (other.Target != mapping.Target)
                {
                    throw new StoreException(
                        StoreProblem.Conflict,
                        $"Mapping '{mapping.Id}' already exists with target '{other.Target}'. Its target cannot be changed to '{mapping.Target}'.");
                }
            }
            else if (string.Equals(other.Target, mapping.Target, StringComparison.OrdinalIgnoreCase))
            {
                var how = other.Target == mapping.Target ? "the same target" : "a target that differs only by letter case";
                throw new StoreException(
                    StoreProblem.Conflict,
                    $"Mapping '{other.Id}' already uses {how} ('{other.Target}'). On Windows the two would be one folder.");
            }
        }

        JsonFiles.Write(MappingPath(mapping.Id), mapping, StorageJsonContext.Default.MappingFile, MappingFile.CurrentSchema);
    }

    public DeviceFile ReadDevice(string deviceName)
    {
        RequireInitialized();
        RequireDeviceName(deviceName);
        var device = JsonFiles.Read(DevicePathOf(deviceName), StorageJsonContext.Default.DeviceFile, DeviceFile.CurrentSchema);
        return CheckDevice(device, deviceName);
    }

    public DeviceFile? TryReadDevice(string deviceName)
    {
        RequireInitialized();
        RequireDeviceName(deviceName);
        return SafeFs.Probe(DevicePathOf(deviceName)) == EntryKind.File ? ReadDevice(deviceName) : null;
    }

    /// <summary>The names of the devices that have a file here, in order.</summary>
    public IReadOnlyList<string> ListDeviceNames()
    {
        RequireInitialized();
        return JsonFiles.ListJson(Path.Combine(MetaRoot, DevicesFolder))
            .Select(file => Path.GetFileNameWithoutExtension(file))
            .Where(Identifiers.IsValidDeviceName)
            .ToList();
    }

    public void WriteDevice(DeviceFile device)
    {
        RequireInitialized();
        ArgumentNullException.ThrowIfNull(device);
        RequireDeviceName(device.DeviceName);
        CheckDevice(device, device.DeviceName);
        JsonFiles.Write(DevicePathOf(device.DeviceName), device, StorageJsonContext.Default.DeviceFile, DeviceFile.CurrentSchema);
    }

    /// <summary>All seed markers for a mapping, active or superseded, by device name.</summary>
    public IReadOnlyList<SeedMarker> ReadSeedMarkers(string mappingId)
    {
        RequireInitialized();
        RequireMappingId(mappingId);
        var markers = new List<SeedMarker>();
        foreach (var file in JsonFiles.ListJson(SeedFolderOf(mappingId)))
        {
            var marker = JsonFiles.Read(file, StorageJsonContext.Default.SeedMarker, SeedMarker.CurrentSchema);
            var deviceName = Path.GetFileNameWithoutExtension(file);
            if (marker.MappingId != mappingId || marker.DeviceName != deviceName)
            {
                throw new StoreException(
                    StoreProblem.Invalid,
                    $"The file '{file}' is not valid: it says it is for mapping '{marker.MappingId}' on device '{marker.DeviceName}'.");
            }

            markers.Add(marker);
        }

        return markers;
    }

    public void WriteSeedMarker(SeedMarker marker)
    {
        RequireInitialized();
        ArgumentNullException.ThrowIfNull(marker);
        RequireMappingId(marker.MappingId);
        RequireDeviceName(marker.DeviceName);
        Directory.CreateDirectory(SeedFolderOf(marker.MappingId));
        JsonFiles.Write(
            Path.Combine(SeedFolderOf(marker.MappingId), marker.DeviceName + ".json"),
            marker, StorageJsonContext.Default.SeedMarker, SeedMarker.CurrentSchema);
    }

    /// <summary>
    /// Marks every other device's active marker for the mapping as superseded by
    /// <paramref name="deviceName"/>. Markers are rewritten, never deleted. Returns the devices affected.
    /// </summary>
    public IReadOnlyList<string> SupersedeOtherMarkers(string mappingId, string deviceName, DateTimeOffset when)
    {
        RequireDeviceName(deviceName);
        var superseded = new List<string>();
        foreach (var marker in ReadSeedMarkers(mappingId))
        {
            if (marker.DeviceName != deviceName && marker.IsActive)
            {
                WriteSeedMarker(marker with { SupersededBy = deviceName, SupersededUtc = when });
                superseded.Add(marker.DeviceName);
            }
        }

        return superseded;
    }

    private string MappingPath(string mappingId) => Path.Combine(MetaRoot, MappingsFolder, mappingId + ".json");

    private string DevicePathOf(string deviceName) => Path.Combine(MetaRoot, DevicesFolder, deviceName + ".json");

    private string SeedFolderOf(string mappingId) => Path.Combine(MetaRoot, SeedFolder, mappingId);

    private void RequireInitialized()
    {
        if (!IsInitialized)
        {
            throw new StoreException(
                StoreProblem.NotInitialized,
                $"'{SyncRoot}' is not set up for SaveTether: the folder '{Path.Combine(ToolFolderName, MetaFolderName)}' is missing. " +
                "If this device syncs with one that is already set up, wait for the sync to finish. " +
                "If the sync root is on a removable drive, check that it is connected.");
        }
    }

    private MappingFile CheckMapping(MappingFile mapping, string expectedId)
    {
        if (mapping.Id != expectedId)
        {
            throw new StoreException(
                StoreProblem.Invalid,
                $"The mapping file '{MappingPath(expectedId)}' is not valid: its id is '{mapping.Id}', which does not match its file name.");
        }

        if (Identifiers.DescribeTargetProblem(mapping.Id, mapping.Target) is { } problem)
        {
            throw new StoreException(StoreProblem.Invalid, $"Mapping '{mapping.Id}' is not valid: {problem}");
        }

        return mapping;
    }

    private DeviceFile CheckDevice(DeviceFile device, string expectedName)
    {
        if (device.DeviceName != expectedName)
        {
            throw new StoreException(
                StoreProblem.Invalid,
                $"The device file '{DevicePathOf(expectedName)}' is not valid: its device_name is '{device.DeviceName}', which does not match its file name.");
        }

        if (string.IsNullOrWhiteSpace(device.InstallId) || string.IsNullOrWhiteSpace(device.BackupRoot))
        {
            throw new StoreException(StoreProblem.Invalid, $"The device file for '{expectedName}' is not valid: install_id and backup_root must not be empty.");
        }

        foreach (var (mappingId, path) in device.Paths)
        {
            if (!Identifiers.IsValidMappingId(mappingId))
            {
                throw new StoreException(StoreProblem.Invalid, $"The device file for '{expectedName}' is not valid: '{mappingId}' is not a mapping ID.");
            }

            if (path is null || string.IsNullOrWhiteSpace(path.Source))
            {
                throw new StoreException(StoreProblem.Invalid, $"The device file for '{expectedName}' is not valid: '{mappingId}' has no source path.");
            }
        }

        return device;
    }

    private static void RequireMappingId(string mappingId)
    {
        if (!Identifiers.IsValidMappingId(mappingId))
        {
            throw new StoreException(StoreProblem.Invalid, $"'{mappingId}' is not a valid mapping ID. Expected a form like 'ppsspp.savedata'.");
        }
    }

    private static void RequireDeviceName(string deviceName)
    {
        if (!Identifiers.IsValidDeviceName(deviceName))
        {
            throw new StoreException(
                StoreProblem.Invalid,
                $"'{deviceName}' is not a valid device name. Use lowercase letters, digits, '-' and '_', for example 'deck'.");
        }
    }
}
