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

/// <summary>
/// Which situation a state stands for, where one state covers several that call for different
/// words or a different fix (desktop-app-spec.md, section 5.1).
/// </summary>
public enum HealthReason
{
    /// <summary>Not applied: this device has no save folder set for the mapping.</summary>
    [JsonStringEnumMemberName("no-save-folder")]
    NoSaveFolder,

    /// <summary>Not applied: this device's file has a save folder that is not an absolute path.</summary>
    [JsonStringEnumMemberName("save-folder-not-absolute")]
    SaveFolderNotAbsolute,

    /// <summary>Not applied: the save folder is set, and not linked yet.</summary>
    [JsonStringEnumMemberName("not-linked-yet")]
    NotLinkedYet,

    /// <summary>Replaced: the save folder is a real folder again and holds files, so saves made here are not shared.</summary>
    [JsonStringEnumMemberName("folder-holds-files")]
    FolderHoldsFiles,

    /// <summary>Replaced: the save folder is a real folder again, with no files in it.</summary>
    [JsonStringEnumMemberName("folder-empty")]
    FolderEmpty,

    /// <summary>Replaced: there is nothing at the save folder. The app's folder has most likely moved, taking the link with it.</summary>
    [JsonStringEnumMemberName("folder-missing")]
    FolderMissing,

    /// <summary>Replaced: a file, or something else that is not a folder, is where the save folder was.</summary>
    [JsonStringEnumMemberName("not-a-folder")]
    NotAFolder,

    /// <summary>Broken: the link is in place and the sync root is there, but the shared folder is missing.</summary>
    [JsonStringEnumMemberName("shared-folder-missing")]
    SharedFolderMissing,

    /// <summary>Broken: the link is in place, but the sync root cannot be used (the report says why).</summary>
    [JsonStringEnumMemberName("sync-root-unavailable")]
    SyncRootUnavailable,
}

/// <param name="Source">S on this device, or null when this device has no save folder set for the mapping.</param>
/// <param name="Detail">One sentence saying what the state means here and what to do.</param>
/// <param name="Reason">
/// Which situation the state stands for, for Not applied, Replaced and Broken; null for the other states,
/// which each stand for one situation.
/// </param>
public sealed record MappingHealth(
    string MappingId, HealthState State, string? Source, string Target, Note Detail, HealthReason? Reason = null);

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
        MappingHealth Is(HealthState state, Note detail, HealthReason? reason = null) =>
            new(mapping.Id, state, path?.Source, target, detail, reason);

        if (path is null)
        {
            return Is(
                HealthState.NotApplied,
                new Note("No save folder is set for it on this device.", new NextStep(NextStepKind.SetSaveFolder, mapping.Id)),
                HealthReason.NoSaveFolder);
        }

        if (!Path.IsPathFullyQualified(path.Source))
        {
            return Is(
                HealthState.NotApplied,
                new Note(
                    $"The save folder '{path.Source}' in this device's file is not an absolute path.",
                    new NextStep(NextStepKind.SetSaveFolder, mapping.Id)),
                HealthReason.SaveFolderNotAbsolute);
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
                return Is(
                    HealthState.Broken,
                    "The link is in place, but the shared folder is missing. Check that the sync root is present and has synced.",
                    HealthReason.SharedFolderMissing);
            }

            var seeders = shared.ReadSeedMarkers(mapping.Id).Where(m => m.IsActive).Select(m => m.DeviceName).ToList();
            return seeders.Count > 1
                ? Is(
                    HealthState.DoubleSeed,
                    new Note(
                        $"More than one device made its saves the shared copy ({string.Join(", ", seeders)}).",
                        new NextStep(NextStepKind.MakeThisCopyWin, mapping.Id)))
                : Is(HealthState.Ok, "Linked to the shared folder.");
        }

        if (recorded is not null)
        {
            var (detail, reason) = Replaced(mapping.Id, path.Source, kind);
            return Is(HealthState.Replaced, detail, reason);
        }

        return Is(
            HealthState.NotApplied,
            new Note("Not applied on this device yet.", new NextStep(NextStepKind.Apply, mapping.Id)),
            HealthReason.NotLinkedYet);
    }

    /// <summary>
    /// The state of a mapping this device applied, worked out from its own record alone: for when
    /// the sync root cannot be read, so neither the shared list nor this device's file is there.
    /// A link still pointing where it was made is Broken then, since the sync root it leads into is unusable.
    /// </summary>
    /// <remarks>Why the sync root cannot be used is said once, in the report, not on every mapping.</remarks>
    public static MappingHealth CheckRecorded(string mappingId, AppliedMapping recorded)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mappingId);
        ArgumentNullException.ThrowIfNull(recorded);

        MappingHealth Is(HealthState state, Note detail, HealthReason? reason = null) =>
            new(mappingId, state, recorded.Source, recorded.Target, detail, reason);

        var kind = SafeFs.Probe(recorded.Source);
        if (kind is EntryKind.Symlink or EntryKind.Junction)
        {
            // Resolving goes through the sync root, which may be what cannot be read (a mount that stopped answering, or
            // a link that loops): then the link and the record are compared as written.
            var written = SafeFs.ReadLinkTarget(recorded.Source);
            string linkTarget;
            bool same;
            try
            {
                linkTarget = PathRules.ResolveAll(written);
                same = PathRules.AreSame(linkTarget, PathRules.ResolveAll(recorded.Target));
            }
            catch (IOException)
            {
                linkTarget = written;
                same = PathRules.AreSame(PathRules.Normalize(written), PathRules.Normalize(recorded.Target));
            }

            return same
                ? Is(HealthState.Broken, "The link is in place, but the sync root is not available.", HealthReason.SyncRootUnavailable)
                : Is(HealthState.Retargeted, $"The save folder now links to '{linkTarget}' instead of the shared folder.");
        }

        var (detail, reason) = Replaced(mappingId, recorded.Source, kind);
        return Is(HealthState.Replaced, detail, reason);
    }

    /// <summary>What has taken the link's place at the save folder, and what to say about it.</summary>
    private static (Note Detail, HealthReason Reason) Replaced(string mappingId, string source, EntryKind kind)
    {
        var apply = new NextStep(NextStepKind.Apply, mappingId);
        if (kind == EntryKind.Missing)
        {
            // Nothing there at all: the app's folder has most likely moved, taking the link with it.
            return (
                new Note("There is nothing at the save folder now. The app may have moved.", new NextStep(NextStepKind.FindAppAgain, mappingId)),
                HealthReason.FolderMissing);
        }

        if (kind != EntryKind.Directory)
        {
            return (new Note("Something other than a folder is where the save folder was.", apply), HealthReason.NotAFolder);
        }

        return SafeFs.EnumerateTree(source).Any(e => e.Kind != EntryKind.Directory)
            ? (new Note("The save folder is a real folder again and holds files, so saves made here are not shared.", apply), HealthReason.FolderHoldsFiles)
            : (new Note("The save folder is no longer a link, and holds no files.", apply), HealthReason.FolderEmpty);
    }
}
