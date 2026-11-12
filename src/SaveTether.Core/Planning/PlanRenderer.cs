using System.Globalization;
using System.Text;
using System.Text.Json;
using SaveTether.Core.Model;

namespace SaveTether.Core.Planning;

/// <summary>
/// Prints a plan or a refusal. A dry run is exactly this output: the same plan a real run would
/// execute, so there is no second code path to drift (design 5.3).
/// </summary>
public static class PlanRenderer
{
    private const int MaxListed = 20;

    /// <summary>One JSON object, for the Decky plugin and scripts.</summary>
    public static string ToJson(PlanOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return JsonSerializer.Serialize(outcome, PlanningJsonContext.Default.PlanOutcome);
    }

    public static string ToText(PlanOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var text = new StringBuilder();
        if (outcome.Plan is { } plan)
        {
            WritePlan(text, plan);
        }
        else if (outcome.Refusal is { } refusal)
        {
            WriteRefusal(text, refusal);
        }

        return text.ToString();
    }

    /// <summary>The command-line flag for a choice.</summary>
    public static string FlagFor(SetupChoice choice) => choice switch
    {
        SetupChoice.Copy => "--copy",
        SetupChoice.NoCopy => "--no-copy",
        SetupChoice.AddMissing => "--add-missing",
        _ => "--replace-shared --confirm-replace",
    };

    /// <summary>One sentence saying what a step does.</summary>
    public static string Describe(PlanStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return step.Kind switch
        {
            StepKind.BackUpSource => $"Back up '{step.Path}' and verify the backup",
            StepKind.BackUpTarget => $"Back up the shared folder '{step.Path}' to this device and verify the backup",
            StepKind.EnsureTarget => $"Create the shared folder '{step.Path}' if it does not exist",
            StepKind.ClearTarget => $"Delete the contents of the shared folder '{step.Path}' (this spreads to every device)",
            StepKind.CopyIntoTarget when step.Entries is { } entries =>
                $"Copy into '{step.OtherPath}' only: {string.Join(", ", entries)}",
            StepKind.CopyIntoTarget => $"Copy everything from '{step.Path}' into '{step.OtherPath}'",
            StepKind.RenameAside => $"Rename '{step.Path}' to '{step.OtherPath}'",
            StepKind.CreateLink => $"Link '{step.Path}' to '{step.OtherPath}'",
            StepKind.VerifyLink => $"Check that '{step.Path}' is a link to '{step.OtherPath}'",
            StepKind.WriteSeedMarker => "Record that this device's saves became the shared copy",
            StepKind.SupersedeMarkers => "Mark other devices' earlier records for this mapping as superseded",
            StepKind.WriteState => "Record the result on this device",
            StepKind.DeleteAside => $"Delete '{step.Path}' (its content is in the backup)",
            StepKind.CopyToStaging => $"Copy '{step.Path}' to '{step.OtherPath}' and verify the copy",
            StepKind.RemoveLink => $"Remove the link '{step.Path}' (the shared folder is not touched)",
            StepKind.RenameIntoPlace => $"Rename '{step.Path}' to '{step.OtherPath}'",
            _ => step.Kind.ToString(),
        };
    }

    private static void WritePlan(StringBuilder text, Plan plan)
    {
        switch (plan.Operation)
        {
            case PlanOperation.Revert:
                text.AppendLine(CultureInfo.InvariantCulture, $"Plan to revert {plan.MappingId}:");
                text.AppendLine(CultureInfo.InvariantCulture, $"  save folder:   {plan.Source}");
                text.AppendLine(CultureInfo.InvariantCulture, $"  shared folder: {plan.Target}");
                break;

            case PlanOperation.Restore:
                text.AppendLine(CultureInfo.InvariantCulture, $"Plan to restore a backup of {plan.MappingId}:");
                text.AppendLine(CultureInfo.InvariantCulture, $"  backup:     {plan.Source}");
                text.AppendLine(CultureInfo.InvariantCulture, $"  restore to: {plan.Target}");
                break;

            default:
                var choice = plan.Choice is { } c ? $", {FlagFor(c).Split(' ')[0]}" : string.Empty;
                text.AppendLine(CultureInfo.InvariantCulture, $"Plan for {plan.MappingId} ({Describe(plan.Classification)}{choice}):");
                text.AppendLine(CultureInfo.InvariantCulture, $"  save folder:   {plan.Source}");
                text.AppendLine(CultureInfo.InvariantCulture, $"  shared folder: {plan.Target}");
                break;
        }

        for (var i = 0; i < plan.Steps.Count; i++)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  {i + 1}. {Describe(plan.Steps[i])}");
        }

        foreach (var note in plan.Notes)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  Note: {note}");
        }

        if (plan.NeedsFirstSeedConfirmation)
        {
            text.AppendLine(
                "  Note: No other device has set this mapping up, as far as this device can see. " +
                "If you already set it up elsewhere, wait until that has synced before continuing.");
        }
    }

    private static void WriteRefusal(StringBuilder text, Refusal refusal)
    {
        var what = refusal.Operation switch
        {
            PlanOperation.Revert => "Not reverted",
            PlanOperation.Restore => "Not restored",
            _ => "Not applied",
        };
        text.AppendLine(CultureInfo.InvariantCulture, $"{what}: {refusal.MappingId}. Nothing was changed.");
        text.AppendLine(refusal.Message);

        if (refusal.Failures.Count > 1)
        {
            foreach (var failure in refusal.Failures)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  - {failure.Message}");
            }
        }

        if (refusal.Comparison is { } comparison)
        {
            WriteComparison(text, comparison);
        }

        if (refusal.AllowedChoices.Count > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Choices: {string.Join(" | ", refusal.AllowedChoices.Select(FlagFor))}");
        }

        if (refusal.AssumeSyncedWouldHelp)
        {
            text.AppendLine("If you know the sync has finished, add --assume-synced.");
        }
    }

    private static void WriteComparison(StringBuilder text, Comparison comparison)
    {
        text.AppendLine(CultureInfo.InvariantCulture, $"  This device, newest file: {Format(comparison.LocalNewestUtc)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"  Shared copy, newest file: {Format(comparison.SharedNewestUtc)}");
        WriteGroup(text, "Only on this device", comparison, EntryPresence.OnlyLocal);
        WriteGroup(text, "Only in the shared copy", comparison, EntryPresence.OnlyShared);
        WriteGroup(text, "On both, but different", comparison, EntryPresence.Different);

        var identical = comparison.Entries.Count(e => e.Presence == EntryPresence.Identical);
        if (identical > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  Identical on both: {identical}");
        }
    }

    private static void WriteGroup(StringBuilder text, string title, Comparison comparison, EntryPresence presence)
    {
        var names = comparison.Entries.Where(e => e.Presence == presence).Select(e => e.Name).ToList();
        if (names.Count == 0)
        {
            return;
        }

        var shown = string.Join(", ", names.Take(MaxListed));
        var more = names.Count > MaxListed ? $", and {names.Count - MaxListed} more" : string.Empty;
        text.AppendLine(CultureInfo.InvariantCulture, $"  {title} ({names.Count}): {shown}{more}");
    }

    private static string Format(DateTime? utc) =>
        utc is { } time ? time.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) : "no files";

    private static string Describe(Classification classification) => classification switch
    {
        Classification.AlreadyApplied => "already linked",
        Classification.LinkOnly => "no saves on this device",
        Classification.FirstDevice => "shared location empty",
        Classification.Guard => "shared location already holds saves",
        _ => classification.ToString(),
    };
}
