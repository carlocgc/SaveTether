using System.Text.Json.Serialization;

namespace SaveTether.Core.Model;

/// <summary>How a mapping redirects saves. Phase 1 has links only (design 5.12).</summary>
public enum Mechanism
{
    [JsonStringEnumMemberName("link")]
    Link,
}

/// <summary>The kind of link that was actually created on a device.</summary>
public enum LinkType
{
    [JsonStringEnumMemberName("junction")]
    Junction,

    [JsonStringEnumMemberName("symlink")]
    Symlink,
}

/// <summary>What the owner chose to do with a device's existing saves (design 5.3).</summary>
public enum SetupChoice
{
    [JsonStringEnumMemberName("copy")]
    Copy,

    [JsonStringEnumMemberName("no-copy")]
    NoCopy,

    [JsonStringEnumMemberName("add-missing")]
    AddMissing,

    [JsonStringEnumMemberName("replace-shared")]
    ReplaceShared,
}

/// <summary>
/// Shared, one file per mapping: .savetether/mappings/&lt;id&gt;.json. Read by every device, so it
/// holds no machine paths.
/// </summary>
public sealed record MappingFile
{
    /// <summary>The newest format this version reads. Format 2 added <see cref="EntryDepth"/>.</summary>
    public const int CurrentSchema = 2;

    public const int MaxEntryDepth = 8;

    public required int Schema { get; init; }

    /// <summary>"app.role", for example "ppsspp.savedata".</summary>
    public required string Id { get; init; }

    /// <summary>"app/save folder", relative to &lt;sync root&gt;/savetether/.</summary>
    public required string Target { get; init; }

    public required Mechanism Mechanism { get; init; }

    /// <summary>
    /// How many folder levels down one game's saves sit (design 5.13). Absent means 1: each
    /// entry directly inside the save folder is one unit for the comparison and "add missing".
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? EntryDepth { get; init; }

    /// <summary><see cref="EntryDepth"/> with its default filled in.</summary>
    [JsonIgnore]
    public int Depth => EntryDepth ?? 1;

    /// <summary>
    /// The form that is written to disk. A mapping at depth 1 is written exactly as format 1
    /// was, so a device still running an older SaveTether keeps reading it; only a mapping that
    /// really uses the new field needs every device updated.
    /// </summary>
    public MappingFile Normalized() => Depth switch
    {
        > 1 => this with { Schema = CurrentSchema },
        1 => this with { Schema = 1, EntryDepth = null },
        _ => this, // Not a valid depth. Left as it is, for validation to refuse.
    };
}

/// <summary>One mapping's settings on one device.</summary>
public sealed record DevicePath
{
    /// <summary>The app's save folder on this device: the path that becomes the link.</summary>
    public required string Source { get; init; }

    /// <summary>Process names that mean the app is running.</summary>
    public IReadOnlyList<string> ProcessNames { get; init; } = [];
}

/// <summary>
/// Shared, one file per device: .savetether/devices/&lt;device&gt;.json. Only the owning device acts
/// on it; it is shared so a wiped device can be restored and its paths edited from elsewhere.
/// </summary>
public sealed record DeviceFile
{
    /// <summary>The newest format this version reads. Format 2 added <see cref="Apps"/>.</summary>
    public const int CurrentSchema = 2;

    public required int Schema { get; init; }

    public required string DeviceName { get; init; }

    /// <summary>Identifies the install that owns this file. Two machines sharing a name will disagree on it.</summary>
    public required string InstallId { get; init; }

    public required string BackupRoot { get; init; }

    /// <summary>Keyed by mapping ID. A mapping with no entry is simply not applied on this device.</summary>
    public IReadOnlyDictionary<string, DevicePath> Paths { get; init; } = new Dictionary<string, DevicePath>();

    /// <summary>
    /// Keyed by app ID: how "app add" found that app's save folders on this device, so running it
    /// again can find them after an SD card or install folder changes. Absent when no app was added.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, DeviceApp>? Apps { get; init; }

    /// <summary>
    /// The form that is written to disk. A device file with no app entries is written exactly as
    /// format 1 was, so an older SaveTether keeps reading it; only "app add" moves it to format 2.
    /// </summary>
    public DeviceFile Normalized() => Apps is { Count: > 0 }
        ? this with { Schema = CurrentSchema }
        : this with { Schema = 1, Apps = null };
}

/// <summary>
/// How one app was found on one device: which of its definition's installs, and the values set
/// for this device that the resolver cannot find by looking (design 5.2).
/// </summary>
public sealed record DeviceApp
{
    /// <summary>The install ID from the app's definition.</summary>
    public required string Install { get; init; }

    /// <summary>{install_dir}: the folder a portable app was unpacked into.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InstallDir { get; init; }

    /// <summary>Overrides the SD card found from the mount table.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SdCard { get; init; }

    /// <summary>The Wine prefix for a winpath, overriding what the definition names.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WinePrefix { get; init; }
}

/// <summary>Local, never synced: the only file needed before the shared folder can be read.</summary>
public sealed record BootstrapFile
{
    public const int CurrentSchema = 1;

    public required int Schema { get; init; }

    public required string SyncRoot { get; init; }

    public required string DeviceName { get; init; }

    public required string InstallId { get; init; }
}

/// <summary>A file that was in the shared copy when it was seeded.</summary>
public sealed record SeedFile(string Path, long Size);

/// <summary>
/// Shared, one file per device per mapping: .savetether/seed/&lt;mapping&gt;/&lt;device&gt;.json.
/// Written when a device's saves become the shared copy.
/// </summary>
public sealed record SeedMarker
{
    public const int CurrentSchema = 1;

    public required int Schema { get; init; }

    public required string MappingId { get; init; }

    public required string DeviceName { get; init; }

    public required DateTimeOffset SeededUtc { get; init; }

    /// <summary>The shared copy's files at the moment of seeding. A snapshot, not a live list.</summary>
    public required IReadOnlyList<SeedFile> Files { get; init; }

    /// <summary>Set when another device replaced the shared copy. A superseded marker is ignored.</summary>
    public string? SupersededBy { get; init; }

    public DateTimeOffset? SupersededUtc { get; init; }

    [JsonIgnore]
    public bool IsActive => SupersededBy is null;
}

/// <summary>What was done for one mapping on this device.</summary>
public sealed record AppliedMapping
{
    public required string Source { get; init; }

    public required string Target { get; init; }

    public required LinkType LinkType { get; init; }

    public required SetupChoice SetupChoice { get; init; }

    public required DateTimeOffset AppliedUtc { get; init; }

    public IReadOnlyList<string> BackupIds { get; init; } = [];
}

/// <summary>
/// Local, never synced: state.json. A record of what this device did. It can be rebuilt from
/// disk, so losing it costs nothing.
/// </summary>
public sealed record StateFile
{
    public const int CurrentSchema = 1;

    public required int Schema { get; init; }

    public IReadOnlyDictionary<string, AppliedMapping> Mappings { get; init; } = new Dictionary<string, AppliedMapping>();

    public static StateFile Empty { get; } = new() { Schema = CurrentSchema };
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UseStringEnumConverter = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(MappingFile))]
[JsonSerializable(typeof(DeviceFile))]
[JsonSerializable(typeof(BootstrapFile))]
[JsonSerializable(typeof(SeedMarker))]
[JsonSerializable(typeof(StateFile))]
internal sealed partial class StorageJsonContext : JsonSerializerContext;
