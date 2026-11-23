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

    // Wording (desktop-app-spec.md, principle 8): the sync folder may be a plain folder on one device. Other devices are
    // named only when another is registered ("others"); a sync tool only where a dialog cannot happen without one.

    public const string MaybeArriving =
        "The shared copy lacks files another device shared. If you use a sync tool, let it finish, then check again.";

    public const string NotArrivedEmpty =
        "Another device shared this, but the shared folder here is empty. If you use a sync tool, it may not have arrived yet.";

    public const string SharedFolderMissing =
        "The link is in place, but its shared folder is missing. If you use a sync tool, it may not have arrived yet.";

    /// <summary>Part of the refusal that offers "is closed": the dialog that used to follow it is gone.</summary>
    public static string AssumeClosedWarning(string app) =>
        $"Changing the folder while {app} runs can lose what it writes on exit. Say it is closed only if it is.";

    /// <summary>Part of the refusal that offers "were deleted": the dialog that used to follow it is gone.</summary>
    public const string AssumeSyncedWarning =
        "Go ahead only if those files were deleted on purpose. If they are still arriving, this device would start from an incomplete shared copy.";

    /// <summary>Added where a change to the shared copy reaches other devices, when there are any.</summary>
    public const string OthersGetChange = "Other devices that use the sync folder get this change too.";

    public static string ReplaceTitle(string role) => $"Replace the shared copy of {role}?";

    public const string ReplaceText = "The shared copy is backed up on this device, then replaced with this device's data.";

    /// <summary>Typed to confirm replacing the shared copy, as for every other deletion of saves.</summary>
    public const string ReplacePhrase = "replace shared data";

    /// <summary>
    /// Added when the owner overrode "the shared copy lacks files": those files are not here, so this device has
    /// no backup of them and the replacement can remove them elsewhere.
    /// </summary>
    public const string ReplaceAnywayText = "Files another device shared that have not arrived here are not in the backup and can be lost.";

    public static string FirstSeed(string role) =>
        $"If {role} is set up on another device, cancel until that device's data has arrived here. Otherwise both become the shared copy.";

    /// <summary>
    /// The one dialog before a plan runs. <see cref="Phrase"/> is set when the plan empties the shared folder (a ClearTarget
    /// step): then the same dialog asks for the phrase, so there is no confirmation followed by a second, typed one.
    /// </summary>
    public sealed record ConfirmationView(string Title, IReadOnlyList<string> Sentences, string Button, string? Phrase);

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
            PrecheckKind.AppRunning => (failure.Processes is { } p
                ? $"Close {app} first. SaveTether looked for: {string.Join(", ", p.LookedFor)}. "
                : $"Close {app} first. ") + AssumeClosedWarning(app),
            PrecheckKind.SandboxCannotReach =>
                $"{app} is a Flatpak and cannot write to the shared folder. SaveTether can give it access to the SaveTether folder in the sync folder, and nothing else.",
            PrecheckKind.SourceParentMissing =>
                "The folder that should hold the save folder does not exist. The app may have moved, or not been started yet.",
            PrecheckKind.BackupLocationUnusable => "Backups cannot be written to the backup location.",
            PrecheckKind.NotEnoughSpace => failure.Space is { } s
                ? $"Not enough free space at {s.Path}: {Size(s.NeededBytes)} needed, {Size(s.FreeBytes)} free."
                : "Not enough free space.",
            PrecheckKind.TargetNotLocal =>
                "The sync folder is on a network share, and Windows on this device is set not to follow links to a share.",
            PrecheckKind.SymlinkNotAllowed =>
                "The sync folder is on a network share. To link to it, Windows needs Developer Mode (Settings, System, For developers), or SaveTether run once from a terminal opened as administrator.",
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
                "The shared copy has not arrived",
                [refusal.AssumeSyncedWouldHelp && refusal.Comparison is not null ? MaybeArriving : NotArrivedEmpty, AssumeSyncedWarning],
                [RefusalAction.CheckAgain, RefusalAction.AssumeSynced],
                details),
            RefusalKind.ForeignLink => new RefusalView(
                "Not SaveTether's link",
                ["The save folder is a link SaveTether did not make. Remove it by hand, then apply."],
                [],
                details),
            RefusalKind.BrokenLink => new RefusalView(
                "Shared folder missing",
                [SharedFolderMissing],
                [RefusalAction.CheckAgain],
                details),
            RefusalKind.ChoiceNeeded => new RefusalView(ChoiceTitle(role), [refusal.Message.Text], [], details),
            RefusalKind.ConfirmationNeeded => new RefusalView(ReplaceTitle(role), [ReplaceText], [], details),
            RefusalKind.ChoiceNotAllowed => new RefusalView(
                "Something went wrong", ["SaveTether offered a choice the core does not allow here. Nothing was changed."], [], details),
            _ => throw new ArgumentOutOfRangeException(nameof(refusal), refusal.Kind, "A refusal with no text."),
        };
    }

    public static string ChoiceTitle(string role) => $"{role} already has data on this device";

    /// <summary>The choice dialog's options: exactly the allowed choices, in the core's order, worded per section 9.2.</summary>
    /// <param name="others">Another device is registered, so replacing the shared copy reaches it.</param>
    public static IReadOnlyList<ChoiceOption> Choices(Refusal refusal, string app, bool others)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        var sharedHasData = refusal.Comparison?.Entries.Any(e => e.Presence != EntryPresence.OnlyLocal) == true;
        return refusal.AllowedChoices.Select(choice => choice switch
        {
            SetupChoice.Copy => new ChoiceOption(choice, "Share this device's data", "This device's data becomes the shared copy."),
            SetupChoice.NoCopy => sharedHasData
                ? new ChoiceOption(choice, "Use the shared copy", $"{app} uses the shared copy. This device's data is kept only in the backup.")
                : new ChoiceOption(choice, "Start empty", $"{app} starts with no data. This device's data is kept only in the backup."),
            SetupChoice.AddMissing => new ChoiceOption(
                choice, "Use the shared copy, and add what only this device has",
                "Nothing in the shared copy is changed or deleted."),
            SetupChoice.ReplaceShared => new ChoiceOption(
                choice, "Replace the shared copy with this device's data",
                others ? $"The shared copy is replaced. {OthersGetChange}" : "The shared copy is replaced."),
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

    /// <summary>The core's notes that the confirmation already says in its own words.</summary>
    private static readonly HashSet<string> Restated = new(StringComparer.Ordinal)
    {
        Planner.RevertLeavesSharedNote, Planner.AlreadyAppliedReplaceNote, Planner.ReplaceSharedNote, RestorePlanner.SharedChangesNote,
    };

    /// <summary>The one dialog before a plan runs (sections 9.2, 9.6, 9.7). The backup is said once, here.</summary>
    /// <param name="others">Another device is registered in the sync folder: only then are other devices mentioned.</param>
    /// <param name="assumedSynced">The owner overrode "the shared copy lacks files".</param>
    public static ConfirmationView Confirmation(Plan plan, string role, bool others, bool assumedSynced = false)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var sentences = new List<string>();
        var clears = plan.Steps.Any(s => s.Kind == StepKind.ClearTarget);
        string title, button;
        string? phrase = null;
        switch (plan.Operation)
        {
            case PlanOperation.Revert:
                (title, button) = ($"Give this device its own copy of {role}?", "Revert");
                sentences.Add("This device gets its own copy and the link is removed. The shared copy is not changed.");
                break;
            case PlanOperation.Restore when clears:
                (title, button, phrase) = (BackupsPage.RestoreSharedTitle(role), "Restore", BackupsPage.RestoreSharedPhrase);
                sentences.Add(others ? $"{BackupsPage.RestoreSharedText} {OthersGetChange}" : BackupsPage.RestoreSharedText);
                sentences.Add($"Shared folder: {plan.Target}");
                break;
            case PlanOperation.Restore:
                (title, button) = ($"Restore the backup into {plan.Target}?", "Restore");
                break;
            default:
                if (clears)
                {
                    (title, button, phrase) = (ReplaceTitle(role), "Replace", ReplacePhrase);
                    sentences.Add(others ? $"{ReplaceText} {OthersGetChange}" : ReplaceText);
                    if (assumedSynced)
                    {
                        sentences.Add(ReplaceAnywayText);
                    }
                }
                else if (plan.Classification == Classification.AlreadyApplied && plan.Choice == SetupChoice.ReplaceShared)
                {
                    // Offered only when other devices also claim the shared copy (a double seed), so they are named.
                    (title, button) = ("Make this device's copy the shared copy?", "Use this copy");
                    sentences.Add("No files change: the shared folder as it is becomes the shared copy, and other devices' claims are set aside. " +
                        "Do this only if it holds the data to keep.");
                }
                else
                {
                    (title, button) = ($"Link {role} on this device to the shared folder?", "Apply");
                }

                sentences.Add($"Save folder: {plan.Source}");
                sentences.Add($"Shared folder: {plan.Target}");
                break;
        }

        // The replace and restore texts already say the backup.
        if (!clears && plan.Steps.Any(s => s.Kind is StepKind.BackUpSource or StepKind.BackUpTarget))
        {
            sentences.Add("A backup is taken first.");
        }

        // With no other device registered, nothing else can be making its data the shared copy.
        if (plan.NeedsFirstSeedConfirmation && others)
        {
            sentences.Add(FirstSeed(role));
        }

        sentences.AddRange(plan.Notes.Select(n => n.Text).Where(t => !Restated.Contains(t)));
        return new ConfirmationView(title, sentences, button, phrase);
    }

    /// <summary>One step in plain words, for "Show steps" and the progress list.</summary>
    public static string Step(PlanStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return step.Kind switch
        {
            StepKind.BackUpSource => "Back up this device's data",
            StepKind.BackUpTarget => "Back up the shared copy",
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
        else if (kinds.Contains(PrecheckKind.SymlinkNotAllowed))
        {
            actions.Add(RefusalAction.CheckAgain);
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
