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
    private const string RemovedFolder = "removed";

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
    /// True when the marker folder is present and can be read. Without it nothing may run (design 5.3, invariant 5):
    /// an unmounted drive, a mount that stopped answering, and a sync that has not arrived yet all look like "not
    /// initialized".
    /// </summary>
    public bool IsInitialized => ReadProblem(MetaRoot) is null && SafeFs.Probe(MetaRoot) == EntryKind.Directory;

    /// <summary>
    /// Why <paramref name="folder"/> cannot be read, or null when it can be or is simply not there. A network share or
    /// a FUSE mount whose server or process has gone ("Transport endpoint is not connected") can still look present,
    /// from what the system has cached, until something is read from it; so this opens the folder as well as looking
    /// at it. Found on the Deck with a dead FUSE mount, which setup and status reported as an unexpected error.
    /// </summary>
    public static string? ReadProblem(string folder)
    {
        try
        {
            var resolved = PathRules.ResolveAll(folder);
            if (SafeFs.Probe(resolved) == EntryKind.Directory)
            {
                using var entries = Directory.EnumerateFileSystemEntries(resolved).GetEnumerator();
                entries.MoveNext();
            }

            return null;
        }
        catch (IOException e) when (e is not (FileNotFoundException or DirectoryNotFoundException))
        {
            // The system's own words, without the path it appends (" : '<path>'"), which the caller names already.
            var cut = e.Message.IndexOf(" : '", StringComparison.Ordinal);
            return (cut > 0 ? e.Message[..cut] : e.Message).TrimEnd('.');
        }
    }

    /// <summary>
    /// Why the sync root cannot be used, for when <see cref="IsInitialized"/> is false. A missing
    /// sync root folder and a missing marker folder get different advice.
    /// </summary>
    /// <summary>
    /// Why the sync root cannot be used, in a few words, for when <see cref="IsInitialized"/> is false: "it cannot be
    /// read (...)", "it does not exist", or "the SaveTether folder is not in it". The desktop app's banner B3 puts it
    /// in its one sentence.
    /// </summary>
    public string NotAvailableReason =>
        (ReadProblem(SyncRoot) ?? ReadProblem(MetaRoot)) is { } problem ? $"it cannot be read ({problem})"
        : SafeFs.Probe(PathRules.ResolveAll(SyncRoot)) == EntryKind.Directory ? "the SaveTether folder is not in it"
        : "it does not exist";

    public string NotAvailableMessage =>
        (ReadProblem(SyncRoot) ?? ReadProblem(MetaRoot)) is { } problem
            ? $"The sync root '{SyncRoot}' cannot be read ({problem}). If it is on a network share or a mounted drive, " +
              "reconnect or mount it again."
            : SafeFs.Probe(PathRules.ResolveAll(SyncRoot)) == EntryKind.Directory
                ? $"'{SyncRoot}' is not set up for SaveTether: the folder '{Path.Combine(ToolFolderName, MetaFolderName)}' is missing. " +
                  "If this device syncs with one that is already set up, wait for the sync to finish. " +
                  "If the sync root is on a removable drive, check that it is connected."
                : $"The sync root '{SyncRoot}' does not exist. If it is on a removable drive, check that it is connected.";

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
    /// Adds a mapping, or rewrites one. An existing mapping's target cannot be changed, because
    /// other devices already link to it. Its entry depth can: nothing points at it.
    /// </summary>
    public void WriteMapping(MappingFile mapping)
    {
        RequireInitialized();
        ArgumentNullException.ThrowIfNull(mapping);
        RequireMappingId(mapping.Id);
        mapping = mapping.Normalized();
        CheckMapping(mapping, mapping.Id);

        foreach (var other in ListMappings())
        {
            if (other.Id == mapping.Id)
            {
                if (other.Target != mapping.Target)
                {
                    throw new StoreException(
                        $"Mapping '{mapping.Id}' already exists with target '{other.Target}'. Its target cannot be changed to '{mapping.Target}'.",
                        new ConflictFacts(ConflictKind.TargetDiffers, mapping.Id, Target: other.Target));
                }
            }
            else if (string.Equals(other.Target, mapping.Target, StringComparison.OrdinalIgnoreCase))
            {
                var how = other.Target == mapping.Target ? "the same target" : "a target that differs only by letter case";
                throw new StoreException(
                    $"Mapping '{other.Id}' already uses {how} ('{other.Target}'). On Windows the two would be one folder.",
                    new ConflictFacts(ConflictKind.TargetInUse, other.Id, Target: other.Target));
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
        device = device.Normalized();
        CheckDevice(device, device.DeviceName);
        JsonFiles.Write(DevicePathOf(device.DeviceName), device, StorageJsonContext.Default.DeviceFile, DeviceFile.CurrentSchema);
    }

    /// <summary>Deletes a device's file, through <see cref="SafeFs.RemoveSharedFile"/>. Nothing else of the device's is touched.</summary>
    public void DeleteDevice(string deviceName)
    {
        RequireInitialized();
        RequireDeviceName(deviceName);
        SafeFs.RemoveSharedFile(DataRoot, DevicePathOf(deviceName));
    }

    /// <summary>The shared files that belong to one device and no one else: its seed markers, then its device file. Only those that exist.</summary>
    public IReadOnlyList<string> FilesOf(string deviceName)
    {
        RequireInitialized();
        RequireDeviceName(deviceName);
        var files = ListMappings()
            .Select(m => Path.Combine(SeedFolderOf(m.Id), deviceName + ".json"))
            .Where(f => SafeFs.Probe(f) == EntryKind.File)
            .ToList();
        if (SafeFs.Probe(DevicePathOf(deviceName)) == EntryKind.File)
        {
            files.Add(DevicePathOf(deviceName));
        }

        return files;
    }

    /// <summary>
    /// Every tombstone: the devices that were removed from this sync folder (phase 3.6). One that cannot be read is skipped, so a stray
    /// file in the folder cannot stop "device list" or a removal.
    /// </summary>
    public IReadOnlyList<TombstoneFile> ReadTombstones()
    {
        RequireInitialized();
        var tombstones = new List<TombstoneFile>();
        foreach (var file in JsonFiles.ListJson(Path.Combine(MetaRoot, RemovedFolder)))
        {
            try
            {
                tombstones.Add(JsonFiles.Read(file, StorageJsonContext.Default.TombstoneFile, TombstoneFile.CurrentSchema));
            }
            catch (StoreException)
            {
                // Not one this version can read: left as it is.
            }
        }

        return tombstones;
    }

    public void WriteTombstone(TombstoneFile tombstone)
    {
        RequireInitialized();
        ArgumentNullException.ThrowIfNull(tombstone);
        RequireDeviceName(tombstone.DeviceName);
        var folder = Path.Combine(MetaRoot, RemovedFolder);
        Directory.CreateDirectory(folder);
        JsonFiles.Write(
            Path.Combine(folder, tombstone.DeviceName + ".json"),
            tombstone, StorageJsonContext.Default.TombstoneFile, TombstoneFile.CurrentSchema);
    }

    /// <summary>Deletes the device's seed marker for the mapping, if it has one.</summary>
    public void DeleteSeedMarker(string mappingId, string deviceName)
    {
        RequireInitialized();
        RequireMappingId(mappingId);
        RequireDeviceName(deviceName);
        var file = Path.Combine(SeedFolderOf(mappingId), deviceName + ".json");
        if (SafeFs.Probe(file) == EntryKind.File)
        {
            SafeFs.RemoveSharedFile(DataRoot, file);
        }
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

    /// <summary>
    /// When the device last wrote to the shared folder: the newest of its device file and the seed markers it
    /// wrote for <paramref name="mappingIds"/>, by their times when they were seeded.
    /// </summary>
    public DateTimeOffset LastActivityOf(string deviceName, IEnumerable<string> mappingIds)
    {
        RequireInitialized();
        RequireDeviceName(deviceName);
        var newest = new DateTimeOffset(File.GetLastWriteTimeUtc(DevicePathOf(deviceName)), TimeSpan.Zero);
        foreach (var id in mappingIds)
        {
            var file = Path.Combine(SeedFolderOf(id), deviceName + ".json");
            if (SafeFs.Probe(file) != EntryKind.File)
            {
                continue;
            }

            try
            {
                var seeded = JsonFiles.Read(file, StorageJsonContext.Default.SeedMarker, SeedMarker.CurrentSchema).SeededUtc;
                newest = seeded > newest ? seeded : newest;
            }
            catch (StoreException)
            {
                // Only the device's own marker is read, and an unreadable one just adds nothing: the time is advisory.
            }
        }

        return newest;
    }

    /// <summary>
    /// Deletes a mapping's seed markers and then its mapping file (design 5.3, invariant 2: the tool's own metadata may be retired).
    /// The caller has checked the mapping is unused and dealt with its shared folder. Each delete refuses a link in the way
    /// (<see cref="SafeFs.RemoveSharedFile"/>), and a markers folder that has gained anything is left.
    /// </summary>
    public void RetireMapping(string mappingId)
    {
        RequireInitialized();
        RequireMappingId(mappingId);
        var seed = SeedFolderOf(mappingId);
        if (SafeFs.Probe(seed) == EntryKind.Directory)
        {
            foreach (var marker in Directory.EnumerateFiles(seed))
            {
                SafeFs.RemoveSharedFile(DataRoot, marker);
            }

            SafeFs.RemoveEmptySharedFolder(DataRoot, seed);
        }

        // The folder a delete that stopped part way used for what it had set aside, once it holds nothing.
        SafeFs.RemoveEmptySharedFolder(DataRoot, Path.Combine(MetaRoot, DeletingFolder, mappingId));
        SafeFs.RemoveSharedFile(DataRoot, MappingPath(mappingId));
    }

    /// <summary>Where <c>map delete</c> sets a file aside, in one rename, before it checks the file and deletes it: below <see cref="MetaRoot"/>.</summary>
    public const string DeletingFolder = "deleting";

    private string MappingPath(string mappingId) => Path.Combine(MetaRoot, MappingsFolder, mappingId + ".json");

    private string DevicePathOf(string deviceName) => Path.Combine(MetaRoot, DevicesFolder, deviceName + ".json");

    private string SeedFolderOf(string mappingId) => Path.Combine(MetaRoot, SeedFolder, mappingId);

    private void RequireInitialized()
    {
        if (!IsInitialized)
        {
            throw new StoreException(StoreProblem.NotInitialized, NotAvailableMessage);
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

        if (mapping.Depth is < 1 or > MappingFile.MaxEntryDepth)
        {
            throw new StoreException(
                StoreProblem.Invalid,
                $"Mapping '{mapping.Id}' is not valid: its entry depth is {mapping.Depth}, and must be from 1 to {MappingFile.MaxEntryDepth}.");
        }

        if (mapping.EntryDepth is not null && mapping.Schema < 2)
        {
            throw new StoreException(
                StoreProblem.Invalid,
                $"Mapping '{mapping.Id}' is not valid: it has an entry depth, which needs \"schema\": 2.");
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

        if (device.Apps is not null && device.Schema < 2)
        {
            throw new StoreException(StoreProblem.Invalid, $"The device file for '{expectedName}' is not valid: it has apps, which need \"schema\": 2.");
        }

        foreach (var (appId, app) in device.Apps ?? new Dictionary<string, DeviceApp>())
        {
            if (!Identifiers.IsValidAppId(appId) || app is null || string.IsNullOrWhiteSpace(app.Install))
            {
                throw new StoreException(StoreProblem.Invalid, $"The device file for '{expectedName}' is not valid: app '{appId}' needs a valid ID and an install.");
            }

            // Device files are shared by every device, on either system: each path is checked as absolute on
            // the system of the device that wrote it, which may not be this one.
            foreach (var value in new[] { app.InstallDir, app.SdCard, app.WinePrefix })
            {
                if (value is not null && !PathRules.IsAbsoluteOnAnySystem(value))
                {
                    throw new StoreException(StoreProblem.Invalid, $"The device file for '{expectedName}' is not valid: app '{appId}' has a path that is not absolute: '{value}'.");
                }
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
