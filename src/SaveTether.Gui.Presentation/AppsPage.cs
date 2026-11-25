using SaveTether.Core.Health;
using SaveTether.Core.Planning;

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

    /// <summary>"map remove": forget this device's folder for the mapping, which moves to "Not set up here". Only offered when Ready to link.</summary>
    Remove,

    /// <summary>
    /// "map delete": a mapping no device uses is deleted from the shared folder (its shared copy and its record, on every device's sync folder). Shown as its own
    /// red button, on a "Not set up here" row that says no device has a folder set for it.
    /// </summary>
    Delete,

    /// <summary>Show the core's own sentence for the state.</summary>
    Details,

    /// <summary>Folder gone: Add app's page 2 for this app, to find where it is now (section 8). Needs a definition.</summary>
    FindAppAgain,

    /// <summary>Folder gone, for a folder added by hand: the hand form for this mapping, to give where it is now (section 8).</summary>
    ChooseFolderAgain,

    /// <summary>Not set up here: Add app's page 2 for this app (section 5.1), or the hand form when the app has no definition.</summary>
    AddHere,

    /// <summary>
    /// A definition's app not found where it looks (a portable copy, say, which no Linux install asks for): the hand form for this
    /// mapping, to give its folder on this device directly. Under More, beside Add and Find the app again.
    /// </summary>
    ChooseFolder,

    /// <summary>The Backups page, filtered to this row's app (section 9.7).</summary>
    Backups,
}

/// <summary>One row of the Apps page: a mapping, its sentence and its actions.</summary>
/// <param name="Main">The row's main action, shown as its button; null when the state has none, or none built yet.</param>
/// <param name="Others">The actions under "More", in the spec's order.</param>
/// <param name="Destructive">An action that removes something for good, shown apart as a red button; null when the row has none.</param>
/// <param name="Devices">For a row some other device has a folder for: their names, shown when the sentence is hovered; null when none has.</param>
public sealed record AppRow(MappingState Mapping, string Sentence, RowAction? Main, IReadOnlyList<RowAction> Others, RowAction? Destructive = null, IReadOnlyList<string>? Devices = null);

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
            (HealthState.Ok, _) => (mapping.LinkKind ?? "Link", (RowAction?)null,
                new[] { RowAction.OpenFolder, RowAction.OpenSharedFolder, RowAction.Backups, RowAction.Revert }),
            (HealthState.NotApplied, HealthReason.NoSaveFolder) => (
                SetUpOnSentence(mapping.SetUpOn),
                RowAction.AddHere, mapping.HasDefinition ? new[] { RowAction.ChooseFolder } : []),
            (HealthState.NotApplied, HealthReason.SaveFolderNotAbsolute) => (
                $"This device's file has '{mapping.Health.Source}' as the folder.", null, new[] { RowAction.Details }),
            (HealthState.NotApplied, _) => ("Original", RowAction.Apply, new[] { RowAction.OpenFolder, RowAction.Remove }),
            (HealthState.Replaced, HealthReason.FolderEmpty) => ("The link is gone and the folder is empty.", RowAction.Apply, new[] { RowAction.OpenFolder }),
            (HealthState.Replaced, HealthReason.FolderMissing) => (
                $"Nothing is at {source}. If the app has moved, find it again.",
                mapping.HasDefinition ? RowAction.FindAppAgain : RowAction.ChooseFolderAgain,
                mapping.HasDefinition ? new[] { RowAction.Apply, RowAction.ChooseFolder } : new[] { RowAction.Apply }),
            (HealthState.Replaced, HealthReason.NotAFolder) => (
                $"Something other than a folder is at {source}. Move it away by hand, then apply.", RowAction.OpenFolderParent, new[] { RowAction.Details }),
            (HealthState.Replaced, _) => (
                "The link was replaced by a real folder. Saves made here are not shared.", RowAction.Apply, new[] { RowAction.OpenFolder, RowAction.Backups }),
            (HealthState.Broken, HealthReason.SyncRootUnavailable) => (
                "The sync folder is not available.", RowAction.CheckAgain, new[] { RowAction.OpenFolder }),
            (HealthState.Broken, _) => (
                ActionTexts.SharedFolderMissing, RowAction.CheckAgain,
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

        // No device links this role (none has a folder set for it): nothing links to it, so it can be deleted from the shared folder.
        var unused = mapping is { Health.Reason: HealthReason.NoSaveFolder, SetUpOn.Count: 0 };
        var devices = mapping.Health.Reason == HealthReason.NoSaveFolder && mapping.SetUpOn.Count > 0 ? mapping.SetUpOn : null;
        return new AppRow(mapping, sentence, main, others, unused ? RowAction.Delete : null, devices);
    }

    /// <summary>
    /// What a row says when other devices have a folder for the role: only how many ("3 devices have a link."), so the row stays
    /// one short line however many there are; the names are in <see cref="DevicesTip"/>, shown when the sentence is hovered.
    /// </summary>
    public static string SetUpOnSentence(IReadOnlyList<string> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        return devices.Count switch
        {
            0 => "No device links this role.",
            1 => "1 device has a link.",
            _ => $"{devices.Count} devices have a link.",
        };
    }

    /// <summary>The hover text of that sentence: every device, one per line.</summary>
    public static string DevicesTip(IReadOnlyList<string> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        return string.Join("\n", devices);
    }

    /// <summary>The rows' column names, pinned above the list: the state dot's column and the buttons' have none.</summary>
    public static readonly IReadOnlyList<string> Columns = [string.Empty, "Folder", "State", "Size", "Details"];

    /// <summary>
    /// The folder a row's size is read from: this device's folder (through its link, so a linked one shows the shared copy), or the
    /// shared copy when this device has no folder for it.
    /// </summary>
    public static string SizeFolder(MappingState mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        // A relative folder (a hand-edited file) would be read against the app's working folder, so the shared copy instead.
        return mapping.Health.Source is { } source && Path.IsPathFullyQualified(source) ? source : mapping.Health.Target;
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
        RowAction.Remove => "Remove",
        RowAction.Delete => "Delete",
        RowAction.Details => "Details",
        RowAction.FindAppAgain => "Find the app again",
        RowAction.ChooseFolderAgain => "Choose the folder again",
        RowAction.AddHere => "Add",
        RowAction.ChooseFolder => "Choose the folder myself",
        RowAction.Backups => "Backups",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "An action with no label."),
    };

    public static string DeleteTitle(string role) => $"Delete {role} from the sync folder?";

    /// <summary>
    /// Delete's confirmation: the title says what. With other devices registered, the owner confirms the condition nothing
    /// here can check: none of them is still writing to the role's shared folder. The result says where the backup is.
    /// </summary>
    public static string DeleteText(bool others) => others
        ? "Its shared copy is backed up on this device first. Other devices that use the sync folder lose it too: make sure none is still writing to it."
        : "Its shared copy is backed up on this device first.";

    public static string DeletedText(string role, bool hadBackup) =>
        $"{role} was deleted from the sync folder." + (hadBackup ? " Its shared copy is in Backups." : string.Empty);

    /// <summary>Remove's confirmation (section 5).</summary>
    public static string RemoveWarning(string role) =>
        $"Forget this device's folder for {role}? Nothing is deleted, and the shared copy is not changed.";

    /// <summary>
    /// A mapping Apply all can ask about together with the others: this device has saves in its folder and the shared
    /// location is empty, so the only question is share them or start empty. Both-sides-have-saves is never batched.
    /// </summary>
    public static bool NeedsFirstDeviceChoice(Refusal refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        return refusal is { Kind: RefusalKind.ChoiceNeeded, Classification: Classification.FirstDevice };
    }

    public static string FirstDeviceTitle(int count, string firstRole) =>
        count == 1 ? $"{firstRole} has data only on this device" : $"{count} save folders have data only on this device";

    public const string FirstDeviceShare = "Share this data";
    public const string FirstDeviceStartEmpty = "Start empty";
    public const string FirstDeviceLeave = "Leave them";

    /// <summary>One question for every mapping with saves only on this device (apply all, section 9.4, as built 2026-11-21).</summary>
    /// <param name="others">Another device is registered: only then can one of these already be set up elsewhere.</param>
    public static string FirstDeviceText(IReadOnlyList<string> roles, bool others) =>
        $"{string.Join("\n", roles)}\n\nNo shared copy yet. One answer for {(roles.Count == 1 ? "it" : "all of them")}:\n" +
        $"{FirstDeviceShare}: this device's data becomes the shared copy.\n" +
        $"{FirstDeviceStartEmpty}: this device's data is kept only in the backup." +
        (others ? $"\n\nIf any of these is set up on another device, choose {FirstDeviceLeave} until its data has arrived here." : string.Empty);

    /// <summary>Apply all's summary (section 9.4).</summary>
    public static string ApplyAllSummary(int linked, IReadOnlyList<string> needYou) =>
        needYou.Count == 0
            ? $"{linked} linked."
            : $"{linked} linked. Not linked, each needs a choice or a fix first: {string.Join(", ", needYou)}.";
}
