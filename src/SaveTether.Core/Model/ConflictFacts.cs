using System.Text.Json.Serialization;

namespace SaveTether.Core.Model;

/// <summary>Which recorded fact a change contradicts.</summary>
public enum ConflictKind
{
    /// <summary>The mapping exists with another shared folder. <see cref="ConflictFacts.Target"/> is the recorded one.</summary>
    [JsonStringEnumMemberName("target-differs")]
    TargetDiffers,

    /// <summary>The mapping is applied on this device at a folder. <see cref="ConflictFacts.Folder"/> is that folder.</summary>
    [JsonStringEnumMemberName("applied-elsewhere")]
    AppliedElsewhere,

    /// <summary>
    /// Another mapping's save folder on this device is the same folder, or contains it, or is inside it.
    /// <see cref="ConflictFacts.MappingId"/> is the other mapping and <see cref="ConflictFacts.Folder"/> its save folder.
    /// </summary>
    [JsonStringEnumMemberName("folder-in-use")]
    FolderInUse,

    /// <summary>
    /// Another mapping has the same shared folder, or one that differs only by letter case.
    /// <see cref="ConflictFacts.MappingId"/> is the other mapping and <see cref="ConflictFacts.Target"/> its shared folder.
    /// </summary>
    [JsonStringEnumMemberName("target-in-use")]
    TargetInUse,
}

/// <summary>
/// The facts behind a conflict, as fields, so a front end can word them itself (design:
/// desktop-app-spec.md, section 13, item 6). The message says the same in words.
/// </summary>
/// <param name="MappingId">The mapping whose record is in the way: this one, or for the "in use" kinds, the other one.</param>
/// <param name="Folder">The save folder on this device, for the kinds that are about one.</param>
/// <param name="Target">The shared folder, as recorded in the mapping, for the kinds that are about one.</param>
public sealed record ConflictFacts(ConflictKind Kind, string MappingId, string? Folder = null, string? Target = null);
