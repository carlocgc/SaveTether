using SaveTether.Core.Operations;

namespace SaveTether.Gui.Presentation;

/// <summary>Page 1 of Remove this device: what is chosen, with the defaults (plan phase 3.6, D4, D5).</summary>
public sealed class RemoveChoicesState
{
    /// <summary>A mapping not named is kept.</summary>
    public Dictionary<string, OrphanChoice> Orphans { get; } = new(StringComparer.Ordinal);

    public bool DeleteBackups { get; set; }

    public bool DeleteEverything { get; set; }

    /// <summary>With D4's option the per-mapping choices do not apply: everything goes.</summary>
    public RemovalChoices ToChoices() => DeleteEverything
        ? new RemovalChoices(null, DeleteBackups, true)
        : new RemovalChoices(new Dictionary<string, OrphanChoice>(Orphans, StringComparer.Ordinal), DeleteBackups);
}

/// <summary>The words and checks of Remove this device. The steps themselves run in the core.</summary>
public static class RemoveDeviceFlow
{
    public const string Title = "Remove this device";
    public const string DeleteEverythingLabel = "Delete everything SaveTether keeps in the sync folder";
    public const string Stays = "Your save folders stay on this device as ordinary folders, with their files.";
    public const string ProgramStays = "The program stays installed. Settings → Uninstall removes it.";
    public const string UnfinishedBanner = "Removing this device did not finish. Finish it to take this device out of the sync folder.";
    public const string FinishRemoval = "Finish removal";
    public const string FinishRemovalHere = "Finish removal here";
    public const string SetUpAgain = "Set up again";

    public static string RemovedByBanner(string by) => $"This device was removed from the sync folder by {by}.";

    public static string OrphanLabel(OrphanChoice choice) => choice switch
    {
        OrphanChoice.Keep => "Keep",
        OrphanChoice.DeleteMapping => "Delete mapping",
        OrphanChoice.DeleteWithSaves => "Delete mapping and saves",
        _ => throw new ArgumentOutOfRangeException(nameof(choice), choice, "A choice with no text."),
    };

    public static string BackupsLabel(bool delete) => delete ? "Delete this device's backups" : "Keep this device's backups";

    /// <summary>D4's option: offered on the last device only. A device file with this install's ID counts as this device.</summary>
    public static (bool Offered, string? Reason) DeleteEverythingOption(MaintenanceOverview overview)
    {
        ArgumentNullException.ThrowIfNull(overview);
        var id = overview.Devices.SingleOrDefault(d => d.IsThisDevice)?.InstallId;
        var others = overview.Devices.Count(d => !d.IsThisDevice && d.InstallId != id);
        return others == 0
            ? (true, null)
            : (false, $"{others} other {(others == 1 ? "device uses" : "devices use")} this sync folder; remove {(others == 1 ? "it" : "them")} first.");
    }

    /// <summary>What is typed before the removal runs (D8): the device's name, then the phrase when everything goes or an orphan's saves do.</summary>
    public static IReadOnlyList<string> PhrasesToType(string deviceName, RemovalChoices choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        var phrases = new List<string> { deviceName };
        if (choices.DeleteEverything || (choices.Orphans?.Values.Any(c => c == OrphanChoice.DeleteWithSaves) ?? false))
        {
            phrases.Add(MaintenancePage.DeleteEverythingPhrase);
        }

        return phrases;
    }

    /// <summary>Page 2: what the removal will do, in the plan's order. A refused plan has <see cref="Refusals"/> instead.</summary>
    public static IReadOnlyList<string> PlanLines(DeviceRemovalPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var lines = new List<string>();
        lines.AddRange(plan.Reverts.Select(r => $"Becomes a folder again: {r.Source}"));
        lines.AddRange(plan.Orphans.Where(o => o.Choice != OrphanChoice.Keep).Select(o => $"{OrphanLabel(o.Choice)}: {o.MappingId}"));
        if (plan.DeleteEverything)
        {
            lines.Add("Deleted: everything SaveTether keeps in the sync folder (mappings, shared save folders, custom definitions).");
        }
        else
        {
            lines.AddRange(plan.SharedFiles.Select(f => $"Deleted from the sync folder: {f}"));
        }

        lines.Add(plan.DeleteBackups ? $"Backups: {BackupCleanupTexts.Count(plan.Backups.Count)} deleted." : "Backups: kept.");
        lines.Add(Stays);
        return lines;
    }

    /// <summary>When the plan is refused: each mapping's title and sentences. Nothing runs.</summary>
    public static IReadOnlyList<string> Refusals(DeviceRemovalPlan plan, Func<string, (string Role, string App)> names)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(names);
        return plan.Refusals.SelectMany(r =>
        {
            var (role, app) = names(r.MappingId);
            var view = ActionTexts.Describe(r, role, app, false);
            return view.Sentences.Prepend(view.Title);
        }).ToList();
    }

    /// <summary>The result's text and its selectable details: the folders given back. A failed revert says nothing shared was deleted.</summary>
    public static (string Text, IReadOnlyList<string> Details) Result(DeviceRemovalResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Failure is not null)
        {
            return ("A folder could not be copied back, so nothing in the sync folder was deleted. Run Remove this device again after fixing it.", result.GivenBack);
        }

        var parts = new List<string>
        {
            $"This device is removed. {(result.GivenBack.Count == 1 ? "1 folder was" : $"{result.GivenBack.Count} folders were")} given back as ordinary folders.",
        };
        if (result.OrphansDeleted.Count > 0)
        {
            parts.Add($"{result.OrphansDeleted.Count} orphaned {(result.OrphansDeleted.Count == 1 ? "mapping" : "mappings")} deleted.");
        }

        if (result.BackupsDeleted > 0)
        {
            parts.Add($"{BackupCleanupTexts.Count(result.BackupsDeleted)} deleted.");
        }

        parts.Add(ProgramStays);
        return (string.Join(' ', parts), result.GivenBack);
    }
}
