using SaveTether.Core.Health;

namespace SaveTether.Gui.Presentation;

/// <summary>What a row can do (desktop-app-spec.md, section 5.1): its main action, or one under "More".</summary>
public enum RowAction
{
    /// <summary>Plan and run an apply.</summary>
    Apply,

    /// <summary>Plan and run a revert.</summary>
    Revert,

    /// <summary>Apply with replace-shared on a linked folder: the way out of a double seed.</summary>
    UseThisCopy,

    /// <summary>Run a health check now.</summary>
    CheckAgain,

    /// <summary>Open the save folder in the file manager.</summary>
    OpenFolder,

    /// <summary>Open the save folder's parent: where something other than a folder is in the way.</summary>
    OpenFolderParent,

    /// <summary>Open the shared folder.</summary>
    OpenSharedFolder,

    /// <summary>Open the shared folder's parent: the shared folder itself is missing.</summary>
    OpenSharedFolderParent,

    /// <summary>"map remove": forget this device's folder for the mapping. Only offered when Ready to link.</summary>
    StopUsingHere,

    /// <summary>Show the core's own sentence for the state.</summary>
    Details,

    /// <summary>Folder gone: Add app's page 2 for this app, to find where it is now (section 8). Needs a definition.</summary>
    FindAppAgain,

    /// <summary>Not on this device: Add app's page 2 for this app (section 5.1). Needs a definition.</summary>
    AddHere,

    /// <summary>The Backups page, filtered to this row's app (section 9.7).</summary>
    Backups,
}

/// <summary>One row of the Apps page: a mapping, its sentence and its actions.</summary>
/// <param name="Main">The row's main action, shown as its button; null when the state has none, or none built yet.</param>
/// <param name="Others">The actions under "More", in the spec's order.</param>
public sealed record AppRow(MappingState Mapping, string Sentence, RowAction? Main, IReadOnlyList<RowAction> Others);

/// <summary>An app with its rows (section 5): mappings grouped by the part of the ID before the dot.</summary>
public sealed record AppGroup(string AppName, IReadOnlyList<AppRow> Rows)
{
    public bool NeedsAttention => Rows.Any(r => r.Mapping.Word.NeedsAttention);
}

/// <summary>
/// The Apps page (section 5): apps needing attention first, then the rest by name; inside an app, rows
/// needing attention first. Rows with no folder on this device are listed last, apart, collapsed.
/// </summary>
public sealed record AppsPage(IReadOnlyList<AppGroup> Groups, IReadOnlyList<AppRow> NotOnThisDevice)
{
    /// <summary>What Apply all works on (section 9.4): Ready to link, Not linked and Link missing.</summary>
    public IReadOnlyList<AppRow> ForApplyAll =>
        Groups.SelectMany(g => g.Rows).Where(r => r.Main == RowAction.Apply).ToList();

    public static AppsPage From(OverallStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var rows = status.Mappings.Select(Row).ToList();
        var groups = rows
            .Where(r => r.Mapping.Health.Reason != HealthReason.NoSaveFolder)
            .GroupBy(r => r.Mapping.AppName, StringComparer.Ordinal)
            .Select(g => new AppGroup(
                g.Key,
                g.OrderBy(r => r.Mapping.Word.NeedsAttention ? 0 : 1).ThenBy(r => r.Mapping.Role, StringComparer.OrdinalIgnoreCase).ToList()))
            .OrderBy(g => g.NeedsAttention ? 0 : 1)
            .ThenBy(g => g.AppName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var notHere = rows
            .Where(r => r.Mapping.Health.Reason == HealthReason.NoSaveFolder)
            .OrderBy(r => r.Mapping.AppName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Mapping.Role, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new AppsPage(groups, notHere);
    }

    /// <summary>Section 5.1's table: the sentence, the main action and the others, for each state and situation.</summary>
    public static AppRow Row(MappingState mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        var source = mapping.Health.Source ?? "the save folder";
        var link = mapping.LinkTarget ?? "another folder";
        var (sentence, main, others) = (mapping.Health.State, mapping.Health.Reason) switch
        {
            (HealthState.Ok, _) => ("Saves here are the shared copy.", (RowAction?)null,
                new[] { RowAction.OpenFolder, RowAction.OpenSharedFolder, RowAction.Backups, RowAction.Revert }),
            (HealthState.NotApplied, HealthReason.NoSaveFolder) => (
                mapping.SetUpOn.Count == 0 ? "No device has a folder set for it." : $"Set up on {string.Join(", ", mapping.SetUpOn)}, not here.",
                mapping.HasDefinition ? RowAction.AddHere : (RowAction?)null, []),
            (HealthState.NotApplied, HealthReason.SaveFolderNotAbsolute) => (
                $"This device's file has '{mapping.Health.Source}' as the folder.", null, new[] { RowAction.Details }),
            (HealthState.NotApplied, _) => ("Not linked yet.", RowAction.Apply, new[] { RowAction.OpenFolder, RowAction.StopUsingHere }),
            (HealthState.Replaced, HealthReason.FolderEmpty) => ("The link is gone and the folder is empty.", RowAction.Apply, new[] { RowAction.OpenFolder }),
            (HealthState.Replaced, HealthReason.FolderMissing) => (
                $"Nothing is at {source}. If the app has moved, find it again.",
                mapping.HasDefinition ? RowAction.FindAppAgain : (RowAction?)null, new[] { RowAction.Apply }),
            (HealthState.Replaced, HealthReason.NotAFolder) => (
                $"Something other than a folder is at {source}. Move it away by hand, then apply.", RowAction.OpenFolderParent, new[] { RowAction.Details }),
            (HealthState.Replaced, _) => (
                "The link was replaced by a real folder. Saves made here are not shared.", RowAction.Apply, new[] { RowAction.OpenFolder, RowAction.Backups }),
            (HealthState.Broken, HealthReason.SyncRootUnavailable) => (
                "The sync folder is not available.", RowAction.CheckAgain, new[] { RowAction.OpenFolder }),
            (HealthState.Broken, _) => (
                "The link is in place, but the shared folder is missing. It may not have synced yet.", RowAction.CheckAgain,
                new[] { RowAction.OpenFolder, RowAction.OpenSharedFolderParent }),
            (HealthState.Retargeted, _) => (
                $"The folder now links to {link} instead of the shared folder. Remove that link by hand, then apply.", RowAction.OpenFolder,
                new[] { RowAction.Details }),
            (HealthState.DoubleSeed, _) => ("More than one device made its saves the shared copy.", RowAction.UseThisCopy, new[] { RowAction.Backups, RowAction.Details }),
            (HealthState.ForeignLink, _) => (
                $"The folder is a link SaveTether did not make, to {link}. Remove it by hand, then apply.", RowAction.OpenFolder,
                new[] { RowAction.Details }),
            _ => throw new ArgumentOutOfRangeException(nameof(mapping), mapping.Health.State, "A state with no row in section 5.1."),
        };
        return new AppRow(mapping, sentence, main, others);
    }

    public static string Label(RowAction action) => action switch
    {
        RowAction.Apply => "Apply",
        RowAction.Revert => "Revert",
        RowAction.UseThisCopy => "Use this device's copy",
        RowAction.CheckAgain => "Check again",
        RowAction.OpenFolder => "Open folder",
        RowAction.OpenFolderParent => "Open folder's parent",
        RowAction.OpenSharedFolder => "Open shared folder",
        RowAction.OpenSharedFolderParent => "Open shared folder's parent",
        RowAction.StopUsingHere => "Stop using here",
        RowAction.Details => "Details",
        RowAction.FindAppAgain => "Find the app again",
        RowAction.AddHere => "Add",
        RowAction.Backups => "Backups",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "An action with no label."),
    };

    /// <summary>"Stop using here"'s confirmation (section 5).</summary>
    public static string StopUsingWarning(string role) =>
        $"Forget this device's folder for {role}? Nothing is deleted; the shared copy and other devices are not affected.";

    /// <summary>Apply all's summary (section 9.4).</summary>
    public static string ApplyAllSummary(int linked, IReadOnlyList<string> needYou) =>
        needYou.Count == 0 ? $"{linked} linked." : $"{linked} linked. {needYou.Count} need you: {string.Join(", ", needYou)}.";
}
