using System.Text.Json.Serialization;

namespace SaveTether.Core.Definitions;

public enum AppKind
{
    Emulator,
    Game,
}

public enum TargetOs
{
    Windows,
    Linux,
}

/// <summary>What a role's folder holds. It decides which roles are on by default and what the owner is told.</summary>
public enum RoleClass
{
    [JsonStringEnumMemberName("save")]
    Save,
    [JsonStringEnumMemberName("system")]
    System,
    [JsonStringEnumMemberName("state")]
    State,
    [JsonStringEnumMemberName("cache")]
    Cache,
    [JsonStringEnumMemberName("mod")]
    Mod,
    [JsonStringEnumMemberName("media")]
    Media,
}

/// <summary>Who vouches for a definition: written and tried by hand, or converted from another source and not tried.</summary>
public enum DefinitionTier
{
    Verified,
    Imported,
}

/// <summary>Store IDs of the app, for search and for finding it by ID. Only Steam's app ID for now.</summary>
public sealed record StoreIds(long? Steam = null);

/// <summary>How an install of the app is recognised on a device. Exactly one field is set.</summary>
public sealed record DetectRule(string? Dir = null, string? File = null, string? Flatpak = null, long? Steam = null);

/// <summary>Which Wine prefix a Windows path is inside, on Linux. Exactly one field is set.</summary>
public sealed record PrefixRule(long? Steam = null, string? SteamShortcut = null, string? Lutris = null, string? Path = null);

/// <summary>
/// The folder that role paths are relative to: a plain path with tokens, or a Windows path that
/// on Linux is looked up inside a Wine prefix (design 5.2, "winpath").
/// </summary>
/// <param name="Path">Set for a plain location.</param>
/// <param name="WinPath">Set for a Windows path.</param>
/// <param name="Prefix">
/// For a Windows path: which Wine prefix, by kind. Null when the definition names none; the prefix
/// set for the device is used, and the install is refused until one is set.
/// </param>
public sealed record BaseLocation(string? Path = null, string? WinPath = null, PrefixRule? Prefix = null);

/// <summary>One way the app can exist on a device.</summary>
public sealed record InstallDefinition(string Id, IReadOnlyList<TargetOs> Os, DetectRule Detect, BaseLocation Base);

/// <summary>One folder of the app that can be shared.</summary>
/// <param name="Paths">
/// The folder, relative to the install's base, by install ID. An install that is not listed does
/// not have this role.
/// </param>
/// <param name="Target">The folder's name under &lt;sync root&gt;/savetether/&lt;app id&gt;/.</param>
public sealed record RoleDefinition(
    string Id,
    RoleClass Class,
    bool Default,
    IReadOnlyDictionary<string, string> Paths,
    string Target,
    int EntryDepth,
    IReadOnlyList<string> Ignore,
    string? Notes)
{
    /// <summary>The ID of the mapping this role becomes: "app.role".</summary>
    public string MappingIdFor(string appId) => $"{appId}.{Id}";

    /// <summary>The mapping's target: "app/folder", relative to &lt;sync root&gt;/savetether/.</summary>
    public string MappingTargetFor(string appId) => $"{appId}/{Target}";
}

/// <summary>
/// What SaveTether knows about one app (design 5.2): the ways it can be installed and the folders
/// worth sharing. Read from a hand-written &lt;app id&gt;.jsonc file by <see cref="DefinitionLoader"/>.
/// </summary>
public sealed record AppDefinition(
    int Schema,
    string Id,
    string Name,
    AppKind Kind,
    IReadOnlyList<InstallDefinition> Installs,
    IReadOnlyList<RoleDefinition> Roles,
    IReadOnlyList<string> Quirks,
    DefinitionTier Tier = DefinitionTier.Verified,
    string? Origin = null,
    StoreIds? StoreIds = null,
    IReadOnlyList<string>? CloudSaves = null,
    IReadOnlyList<string>? TestedOn = null)
{
    public const int CurrentSchema = 1;

    public const string FileExtension = ".jsonc";

    /// <summary>The stores whose copies of a game can have cloud saves, as "cloud_saves" names them.</summary>
    public static readonly IReadOnlyList<string> CloudStores = ["steam", "gog", "epic", "xbox", "ubisoft", "ea"];
}

/// <summary>One problem in a definition file, with where it is.</summary>
public sealed record DefinitionError(string File, int Line, string Message)
{
    public override string ToString() => $"{File}({Line}): {Message}";
}

/// <summary>A definition, or the reasons there is none. Never both.</summary>
public sealed record DefinitionLoadResult(AppDefinition? Definition, IReadOnlyList<DefinitionError> Errors)
{
    public bool IsValid => Definition is not null;
}
