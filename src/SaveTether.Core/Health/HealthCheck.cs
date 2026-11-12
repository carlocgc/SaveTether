using System.Text.Json.Serialization;
using SaveTether.Core.FileSystem;
using SaveTether.Core.Model;
using SaveTether.Core.Storage;

namespace SaveTether.Core.Health;

/// <summary>The state of one mapping on this device (design 5.5).</summary>
public enum HealthState
{
    /// <summary>S links to T, and T exists.</summary>
    [JsonStringEnumMemberName("ok")]
    Ok,

    /// <summary>The mapping is in the shared list but is not applied here.</summary>
    [JsonStringEnumMemberName("not-applied")]
    NotApplied,

    /// <summary>It was applied here, but S is a real folder again.</summary>
    [JsonStringEnumMemberName("replaced")]
    Replaced,

    /// <summary>S links to T, but T is missing.</summary>
    [JsonStringEnumMemberName("broken")]
    Broken,

    /// <summary>It was applied here, but S now links somewhere else.</summary>
    [JsonStringEnumMemberName("retargeted")]
    Retargeted,

    /// <summary>More than one device has an active seed marker for the mapping.</summary>
    [JsonStringEnumMemberName("double-seed")]
    DoubleSeed,

    /// <summary>S is a link SaveTether did not make.</summary>
    [JsonStringEnumMemberName("foreign-link")]
    ForeignLink,
}

/// <param name="Source">S on this device, or null when this device has no save folder set for the mapping.</param>
/// <param name="Detail">One sentence saying what the state means here and what to do.</param>
public sealed record MappingHealth(string MappingId, HealthState State, string? Source, string Target, string Detail);

/// <summary>
/// Works out a mapping's state from one look at S, one read of its link, and the seed markers.
/// It only reads.
/// </summary>
public static class HealthCheck
{
    /// <param name="path">This device's entry for the mapping, or null when it has none.</param>
    /// <param name="recorded">What the state file says was applied here, or null.</param>
    public static MappingHealth Check(SharedStore shared, MappingFile mapping, DevicePath? path, AppliedMapping? recorded)
    {
        ArgumentNullException.ThrowIfNull(shared);
        ArgumentNullException.ThrowIfNull(mapping);

        var target = shared.TargetPathOf(mapping);
        MappingHealth Is(HealthState state, string detail) => new(mapping.Id, state, path?.Source, target, detail);

        if (path is null)
        {
            return Is(HealthState.NotApplied, $"No save folder is set for it on this device. Set one with 'savetether map add {mapping.Id} --source <path>'.");
        }

        if (!Path.IsPathFullyQualified(path.Source))
        {
            return Is(HealthState.NotApplied, $"The save folder '{path.Source}' in this device's file is not an absolute path.");
        }

        var kind = SafeFs.Probe(path.Source);
        if (kind is EntryKind.Symlink or EntryKind.Junction)
        {
            var linkTarget = PathRules.ResolveAll(SafeFs.ReadLinkTarget(path.Source));
            if (!PathRules.AreSame(linkTarget, PathRules.ResolveAll(target)))
            {
                return recorded is null
                    ? Is(HealthState.ForeignLink, $"The save folder is a link to '{linkTarget}', which SaveTether did not make. Remove it by hand before applying.")
                    : Is(HealthState.Retargeted, $"The save folder now links to '{linkTarget}' instead of the shared folder.");
            }

            if (SafeFs.Probe(PathRules.ResolveAll(target)) != EntryKind.Directory)
            {
                return Is(HealthState.Broken, "The link is in place, but the shared folder is missing. Check that the sync root is present and has synced.");
            }

            var seeders = shared.ReadSeedMarkers(mapping.Id).Where(m => m.IsActive).Select(m => m.DeviceName).ToList();
            return seeders.Count > 1
                ? Is(
                    HealthState.DoubleSeed,
                    $"More than one device made its saves the shared copy ({string.Join(", ", seeders)}). " +
                    "On the device that should win, run 'savetether apply " + mapping.Id + " --replace-shared', or restore a backup.")
                : Is(HealthState.Ok, "Linked to the shared folder.");
        }

        if (recorded is not null)
        {
            var stranded = kind == EntryKind.Directory && SafeFs.EnumerateTree(path.Source).Any(e => e.Kind != EntryKind.Directory);
            return Is(
                HealthState.Replaced,
                stranded
                    ? "The save folder is a real folder again and holds files, so saves made here are not shared. Run apply to link it again."
                    : "The save folder is no longer a link. Run apply to link it again.");
        }

        return Is(HealthState.NotApplied, "Not applied on this device yet. Run 'savetether apply " + mapping.Id + "'.");
    }
}
