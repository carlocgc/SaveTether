using System.Globalization;
using SaveTether.Core.Execution;
using SaveTether.Core.Model;
using SaveTether.Core.Planning;

namespace SaveTether.Gui.Presentation;

/// <summary>What a refusal lets the owner do, beyond Cancel (desktop-app-spec.md, section 9.2).</summary>
public enum RefusalAction
{
    /// <summary>Plan again: the app may have closed, the sync may have arrived.</summary>
    CheckAgain,

    /// <summary>"The app is closed": plan again with <see cref="ApplyRequest.AssumeClosed"/>, after a confirmation.</summary>
    AssumeClosed,

    /// <summary>"The shared saves really were deleted": plan again with <see cref="ApplyRequest.AssumeSynced"/>, after a confirmation.</summary>
    AssumeSynced,

    /// <summary>Give the Flatpak app access to the SaveTether folder, then plan again.</summary>
    GiveAccess,

    /// <summary>A restore refused because the save folder is a link (section 9.7): revert the mapping instead.</summary>
    Revert,
}

/// <summary>A refusal as the window shows it: the app's sentences, what can be done, and the core's message under Details.</summary>
public sealed record RefusalView(string Title, IReadOnlyList<string> Sentences, IReadOnlyList<RefusalAction> Actions, string Details);

/// <summary>One setup choice as the choice dialog offers it.</summary>
public sealed record ChoiceOption(SetupChoice Choice, string Label, string Sentence);

/// <summary>
/// The app's own words for the core's refusals, choices, steps and results (desktop-app-spec.md,
/// principle 3 and section 9). Every value of each enum has a text; a test fails for one without.
/// </summary>
public static class ActionTexts
{
    public const string EveryChoiceBacksUp = "This device's data is backed up first, whichever you choose.";

    public const string MaybeArriving =
        "The shared copy may still be arriving: it lacks files a device shared. Wait for the sync tool, or confirm it has finished.";

    public static string AssumeClosedWarning(string app) =>
        $"SaveTether thinks {app} is running. Linking while it runs can lose the data it writes on exit. Go ahead only if it is closed.";

    public const string AssumeSyncedWarning =
        "Go ahead only if the shared data was deleted on purpose. If the sync tool is still delivering them, wait instead: " +
        "going ahead now would start this device from an empty shared folder.";

    public static string ReplaceTitle(string role) => $"Replace the shared data for {role} on every device?";

    public const string ReplaceText =
        "The shared copy is backed up on this device first. Then it is deleted and replaced with this device's data. " +
        "The deletion reaches every device through the sync tool.";

    /// <summary>Typed to confirm replacing the shared data, as for every other deletion of saves.</summary>
    public const string ReplacePhrase = "replace shared data";

    /// <summary>The last dialog before a plan that empties the shared folder runs: what it says, its button, and the phrase to type.</summary>
    public sealed record SharedDeletionGuardView(string Title, string Text, string Button, string Phrase);

    /// <summary>The typed guard for a plan, or null when it does not delete what the shared folder holds (no ClearTarget step).</summary>
    public static SharedDeletionGuardView? SharedDeletionGuard(Plan plan, string role)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.Steps.Any(s => s.Kind == StepKind.ClearTarget))
        {
            return null;
        }

        return plan.Operation == PlanOperation.Restore
            ? new(BackupsPage.RestoreSharedTitle(role), BackupsPage.RestoreSharedText, "Restore on every device", BackupsPage.RestoreSharedPhrase)
            : new(ReplaceTitle(role), ReplaceText, "Replace on every device", ReplacePhrase);
    }

    public static string FirstSeed(string role) =>
        $"If {role} was already set up on another device, cancel and wait until it has synced here. " +
        "Otherwise both devices will make their own data the shared copy.";

    /// <summary>Section 9.2's precondition table: one sentence for each kind.</summary>
    /// <param name="role">The mapping, as the row names it: "Eden saves".</param>
    /// <param name="app">The app, for what concerns the program rather than its folder: "Eden".</param>
    public static string Precondition(PrecheckFailure failure, string role, string app)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return failure.Kind switch
        {
            PrecheckKind.SyncRootNotInitialized => "The sync folder is not available.",
            PrecheckKind.UnfinishedOperation => "An earlier operation did not finish. Recover it first.",
            PrecheckKind.AppRunning => failure.Processes is { } p
                ? $"Close {app} first. SaveTether looked for: {string.Join(", ", p.LookedFor)}."
                : $"Close {app} first.",
            PrecheckKind.SandboxCannotReach =>
                $"{app} is a Flatpak and cannot write to the shared folder. SaveTether can give it access to the SaveTether folder in the sync folder, and nothing else.",
            PrecheckKind.SourceParentMissing =>
                "The folder that should hold the save folder does not exist. The app may have moved, or not been started yet.",
            PrecheckKind.BackupLocationUnusable => "Backups cannot be written to the backup location.",
            PrecheckKind.NotEnoughSpace => failure.Space is { } s
                ? $"Not enough free space at {s.Path}: {Size(s.NeededBytes)} needed, {Size(s.FreeBytes)} free."
                : "Not enough free space.",
            PrecheckKind.TargetNotLocal =>
                "On Windows the sync folder must be on a local drive, because a folder link cannot point to a network drive.",
            PrecheckKind.SourceNotLocal =>
                $"{role}'s folder is on a network drive. On Windows SaveTether can only link a folder on a local drive.",
            PrecheckKind.TargetOutsideDataRoot or PrecheckKind.SourceInsideSyncRoot or PrecheckKind.PathsOverlap
                or PrecheckKind.SourceUnusable or PrecheckKind.TargetUnusable => $"{role} cannot be linked as it is set up.",
            PrecheckKind.NotApplied => "The folder is no longer this mapping's link, so there is nothing to revert.",
            PrecheckKind.BackupUnusable => "This backup is incomplete or damaged, so it cannot be restored.",
            // Only the link case names Revert as the way out; the others (not a folder, its parent gone, a shared
            // folder that is no longer a save folder) leave copying by hand from the backup's folder.
            PrecheckKind.RestoreDestinationUnusable => failure.Message.Next.Any(n => n.Kind == NextStepKind.Revert)
                ? $"{role} is linked, so restoring would write into the shared copy. Revert first."
                : "The folder this backup was taken from cannot be restored into as it is now. Its files can still be copied back by hand: open the backup's folder.",
            _ => throw new ArgumentOutOfRangeException(nameof(failure), failure.Kind, "A precondition with no text."),
        };
    }

    /// <summary>A refusal as a dialog shows it. <see cref="RefusalKind.ChoiceNeeded"/> and <see cref="RefusalKind.ConfirmationNeeded"/> have their own dialogs.</summary>
    public static RefusalView Describe(Refusal refusal, string role, string app, bool sandboxGrantPossible)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        string[] messages = [refusal.Message.Text, .. refusal.Failures.Select(f => f.Message.Text)];
        var details = string.Join("\n", messages.Distinct(StringComparer.Ordinal));
        return refusal.Kind switch
        {
            RefusalKind.Precondition => new RefusalView(
                $"{role} cannot be {Done(refusal.Operation)} yet",
                refusal.Failures.Select(f => Precondition(f, role, app)).Distinct(StringComparer.Ordinal).ToList(),
                PreconditionActions(refusal, sandboxGrantPossible),
                details),
            RefusalKind.NotArrived => new RefusalView(
                "The shared data has not arrived",
                [refusal.AssumeSyncedWouldHelp && refusal.Comparison is not null
                    ? MaybeArriving
                    : "A device has shared this data, but the shared folder here is still empty. The sync tool has probably not delivered it yet."],
                [RefusalAction.CheckAgain, RefusalAction.AssumeSynced],
                details),
            RefusalKind.ForeignLink => new RefusalView(
                "Not SaveTether's link",
                ["The save folder is a link SaveTether did not make. Remove it by hand, then apply."],
                [],
                details),
            RefusalKind.BrokenLink => new RefusalView(
                "Shared folder missing",
                ["The link is in place, but the shared folder is missing. It may not have synced yet."],
                [RefusalAction.CheckAgain],
                details),
            RefusalKind.ChoiceNeeded => new RefusalView($"{role} on this device already has data.", [refusal.Message.Text], [], details),
            RefusalKind.ConfirmationNeeded => new RefusalView(ReplaceTitle(role), [ReplaceText], [], details),
            RefusalKind.ChoiceNotAllowed => new RefusalView(
                "Something went wrong", ["SaveTether offered a choice the core does not allow here. Nothing was changed."], [], details),
            _ => throw new ArgumentOutOfRangeException(nameof(refusal), refusal.Kind, "A refusal with no text."),
        };
    }

    /// <summary>The choice dialog's options: exactly the allowed choices, in the core's order, worded per section 9.2.</summary>
    public static IReadOnlyList<ChoiceOption> Choices(Refusal refusal, string app)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        var sharedHasData = refusal.Comparison?.Entries.Any(e => e.Presence != EntryPresence.OnlyLocal) == true;
        return refusal.AllowedChoices.Select(choice => choice switch
        {
            SetupChoice.Copy => new ChoiceOption(choice, "Share this device's data", "This data becomes the shared copy for every device."),
            SetupChoice.NoCopy => sharedHasData
                ? new ChoiceOption(choice, "Use the shared data", $"This device's data is kept in a backup only. {app} will see the shared data.")
                : new ChoiceOption(choice, "Start empty", $"This device's data is kept in a backup only. {app} will see no data until another device shares some."),
            SetupChoice.AddMissing => new ChoiceOption(
                choice, "Use the shared data, and add games only this device has",
                "Copies entries the shared copy does not have. Nothing in the shared copy is changed or deleted."),
            SetupChoice.ReplaceShared => new ChoiceOption(
                choice, "Replace the shared data with this device's",
                "Every device's copy is replaced. The shared data is backed up on this device first."),
            _ => throw new ArgumentOutOfRangeException(nameof(refusal), choice, "A choice with no text."),
        }).ToList();
    }

    /// <summary>The comparison's groups as section 9.2 names them, with each entry.</summary>
    public static IReadOnlyList<(string Group, IReadOnlyList<string> Entries)> ComparisonGroups(Comparison comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        (EntryPresence Presence, string Name)[] groups =
        [
            (EntryPresence.OnlyLocal, "Only on this device"),
            (EntryPresence.OnlyShared, "Only in the shared copy"),
            (EntryPresence.Different, "On both, different"),
            (EntryPresence.Identical, "On both, the same"),
        ];
        var result = new List<(string Group, IReadOnlyList<string> Entries)>();
        foreach (var (presence, name) in groups)
        {
            var entries = comparison.Entries.Where(e => e.Presence == presence).Select(e => e.Name).ToList();
            if (entries.Count > 0)
            {
                // Every group is collapsed in the dialog, so its heading says how many.
                result.Add(($"{name} ({entries.Count})", entries));
            }
        }

        return result;
    }

    /// <summary>The newest file on each side, for above the comparison.</summary>
    public static string Newest(Comparison comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        static string When(DateTime? utc) => utc is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "none";
        return $"Newest here: {When(comparison.LocalNewestUtc)}. Newest in the shared copy: {When(comparison.SharedNewestUtc)}.";
    }

    /// <summary>The confirmation before a plan runs (sections 9.2, 9.6).</summary>
    public static (string Title, IReadOnlyList<string> Sentences) Confirmation(Plan plan, string role)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var sentences = new List<string>();
        string title;
        switch (plan.Operation)
        {
            case PlanOperation.Revert:
                title = $"Give this device its own copy of {role}?";
                sentences.Add("The shared data is copied here and the link is removed. Other devices keep sharing.");
                break;
            case PlanOperation.Restore:
                title = $"Restore the backup into {plan.Target}?";
                break;
            default:
                title = plan.Classification == Classification.AlreadyApplied && plan.Choice == SetupChoice.ReplaceShared
                    ? "Make this device's copy the one every device keeps?"
                    : $"Link {role} on this device to the shared folder?";
                if (plan.Classification == Classification.AlreadyApplied && plan.Choice == SetupChoice.ReplaceShared)
                {
                    sentences.Add("No files change; the other devices' claims are marked as replaced.");
                }

                sentences.Add($"Save folder: {plan.Source}");
                sentences.Add($"Shared folder: {plan.Target}");
                break;
        }

        if (plan.Steps.Any(s => s.Kind is StepKind.BackUpSource or StepKind.BackUpTarget))
        {
            sentences.Add("A backup is taken first.");
        }

        if (plan.NeedsFirstSeedConfirmation)
        {
            sentences.Add(FirstSeed(role));
        }

        sentences.AddRange(plan.Notes.Select(n => n.Text));
        return (title, sentences);
    }

    /// <summary>One step in plain words, for "Show steps" and the progress list.</summary>
    public static string Step(PlanStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return step.Kind switch
        {
            StepKind.BackUpSource => "Back up this device's data",
            StepKind.BackUpTarget => "Back up the shared data",
            StepKind.EnsureTarget => "Make sure the shared folder exists",
            StepKind.ClearTarget => "Empty the shared folder",
            StepKind.CopyIntoTarget => step.Entries is { Count: > 0 } entries
                ? $"Copy {string.Join(", ", entries)} into the shared folder"
                : "Copy this device's data into the shared folder",
            StepKind.RenameAside => "Move the save folder aside",
            StepKind.CreateLink => "Link the save folder to the shared folder",
            StepKind.VerifyLink => "Check the link",
            StepKind.WriteSeedMarker => "Record this device's data as the shared copy",
            StepKind.SupersedeMarkers => "Mark other devices' shared copies as replaced",
            StepKind.WriteState => "Record what was done",
            StepKind.DeleteAside => "Remove the folder moved aside (it is in the backup)",
            StepKind.CopyToStaging => "Copy the data to a new folder beside the save folder",
            StepKind.RemoveLink => "Remove the link",
            StepKind.RenameIntoPlace => "Put the copy in place of the save folder",
            StepKind.RecreateLinks => "Recreate the links the backup recorded",
            _ => throw new ArgumentOutOfRangeException(nameof(step), step.Kind, "A step with no text."),
        };
    }

    /// <summary>
    /// The finished dialog of section 9.3: the result, with the plan's steps kept above it. A run with no plan
    /// (recover) or a refused one (nothing ran) has no steps, so nothing but the result's sentences is shown.
    /// </summary>
    public static (string Title, IReadOnlyList<string> Steps, IReadOnlyList<string> Sentences) FinishedView(Plan? plan, ExecutionResult result, string role, bool recovery = false)
    {
        var (title, sentences) = Result(result, role, recovery);
        return (title, plan is null || result.Status == ExecutionStatus.Refused ? [] : plan.Steps.Select(Step).ToList(), sentences);
    }

    /// <summary>A result as section 9.3 shows it.</summary>
    /// <param name="recovery">
    /// True for <c>Recover</c>'s result. Then Completed means the unfinished operation was carried through,
    /// and RolledBack means it was undone: recovery succeeded either way, so neither is worded as a failure.
    /// </param>
    public static (string Title, IReadOnlyList<string> Sentences) Result(ExecutionResult result, string role, bool recovery = false)
    {
        ArgumentNullException.ThrowIfNull(result);
        var sentences = result.Messages.Select(m => m.Text).ToList();
        sentences.AddRange(result.BackupIds.Select(id => $"Backup taken: {id}"));
        var operation = result.Operation switch
        {
            PlanOperation.Revert => "Revert",
            PlanOperation.Restore => "Restore",
            _ => "Apply",
        };
        var title = (result.Status, recovery) switch
        {
            (ExecutionStatus.Completed, true) => $"Recovered: the unfinished {operation.ToLowerInvariant()} was finished.",
            (ExecutionStatus.RolledBack, true) => $"Recovered: the unfinished {operation.ToLowerInvariant()} was undone. Nothing has changed.",
            (ExecutionStatus.Completed, _) => result.Operation switch
            {
                PlanOperation.Revert => $"{role} has its own copy again.",
                PlanOperation.Restore => "The backup is restored.",
                _ => $"{role} is linked.",
            },
            (ExecutionStatus.Refused, _) => "Nothing was changed.",
            (ExecutionStatus.RolledBack, _) => $"{operation} failed and was undone. Nothing has changed.",
            (ExecutionStatus.RecoveryNeeded, _) => $"{operation} stopped part way. Recover it before doing anything else.",
            (ExecutionStatus.NothingToRecover, _) => "Nothing to recover: no operation is unfinished.",
            _ => throw new ArgumentOutOfRangeException(nameof(result), result.Status, "A result with no text."),
        };
        if (result.Error is { } error)
        {
            sentences.Insert(0, error.Text);
        }

        return (title, sentences);
    }

    /// <summary>Banner B2's sentence, from the journal's record of the unfinished operation.</summary>
    public static string Unfinished(JournalFile? journal) => journal is null
        ? "An operation did not finish. Nothing else can be done until it is recovered."
        : $"{Operation(journal.Plan.Operation)} of {journal.Plan.MappingId} did not finish. Nothing else can be done until it is recovered.";

    public const string RecoverText =
        "SaveTether either finishes the operation or undoes it, whichever its journal shows is safe. Nothing is lost either way.";

    private static List<RefusalAction> PreconditionActions(Refusal refusal, bool sandboxGrantPossible)
    {
        var kinds = refusal.Failures.Select(f => f.Kind).ToHashSet();
        var actions = new List<RefusalAction>();
        if (kinds.Count > 0 && kinds.All(k => k == PrecheckKind.SandboxCannotReach) && sandboxGrantPossible)
        {
            actions.Add(RefusalAction.GiveAccess);
        }

        if (kinds.Contains(PrecheckKind.AppRunning))
        {
            actions.Add(RefusalAction.CheckAgain);
            actions.Add(RefusalAction.AssumeClosed);
        }

        if (refusal.Failures.Any(f => f.Kind == PrecheckKind.RestoreDestinationUnusable && f.Message.Next.Any(n => n.Kind == NextStepKind.Revert)))
        {
            actions.Add(RefusalAction.Revert);
        }

        return actions;
    }

    private static string Done(PlanOperation operation) => operation switch
    {
        PlanOperation.Revert => "reverted",
        PlanOperation.Restore => "restored",
        _ => "linked",
    };

    private static string Operation(PlanOperation operation) => operation switch
    {
        PlanOperation.Revert => "Revert",
        PlanOperation.Restore => "Restore",
        _ => "Apply",
    };

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GiB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MiB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.0} KiB",
        _ => $"{bytes} bytes",
    };
}
