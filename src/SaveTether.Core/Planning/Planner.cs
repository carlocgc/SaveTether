using SaveTether.Core.FileSystem;
using SaveTether.Core.Model;

namespace SaveTether.Core.Planning;

/// <summary>
/// Decides what to do for one mapping (design 5.3). Pure logic over a <see cref="Snapshot"/>:
/// it reads no files and changes nothing. The result is a plan to run, or a refusal that says
/// why and what the owner can do.
/// </summary>
public static class Planner
{
    // Notes the desktop app says in its own words, so it leaves them out of its confirmation (ActionTexts.Confirmation).
    public const string RevertLeavesSharedNote = "The shared folder is left as it is, for the other devices.";

    public const string AlreadyAppliedReplaceNote =
        "Already linked, so no saves are copied or deleted. The shared folder as it is now becomes the agreed copy, " +
        "and the other devices' records are marked as superseded.";

    public const string ReplaceSharedNote = "The shared saves in the sync folder will be replaced. They are backed up on this device first.";

    public const string AsideSuffix = ".savetether-old";
    public const string StagingSuffix = ".savetether-new";

    private static readonly SetupChoice[] FirstDeviceChoices = [SetupChoice.Copy, SetupChoice.NoCopy];
    private static readonly SetupChoice[] GuardChoices = [SetupChoice.NoCopy, SetupChoice.AddMissing, SetupChoice.ReplaceShared];

    public static PlanOutcome Plan(Snapshot snapshot, ApplyRequest request)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(request);

        var failures = StructuralFailures(snapshot, request);
        if (failures.Count > 0)
        {
            return Refuse(
                snapshot, RefusalKind.Precondition, Classification.NotClassified,
                failures.Count == 1 ? failures[0].Message : $"{failures.Count} checks failed. Nothing was changed.",
                failures: failures);
        }

        var classification = Classify(snapshot, request.AssumeSynced);
        var outcome = classification switch
        {
            Classification.AlreadyApplied => PlanAlreadyApplied(snapshot, request),
            Classification.BrokenLink => Refuse(
                snapshot, RefusalKind.BrokenLink, classification,
                $"'{snapshot.Source}' is linked to '{snapshot.Target}', but that folder is missing. " +
                "Check that the sync root is present and has finished syncing."),
            Classification.ForeignLink => Refuse(
                snapshot, RefusalKind.ForeignLink, classification,
                $"'{snapshot.Source}' is a link to '{snapshot.SourceLinkTarget}', which SaveTether did not create. " +
                "Remove that link by hand first (design 5.7); SaveTether will not follow or replace it."),
            Classification.NotArrived => Refuse(
                snapshot, RefusalKind.NotArrived, classification,
                NotArrivedMessage(snapshot),
                assumeSyncedWouldHelp: true),
            Classification.LinkOnly => PlanLinkOnly(snapshot, request),
            Classification.FirstDevice => PlanFirstDevice(snapshot, request),
            _ => PlanGuard(snapshot, request),
        };

        return outcome.Plan is { } plan && SpaceFailures(snapshot, plan) is { Count: > 0 } spaceFailures
            ? Refuse(snapshot, RefusalKind.Precondition, classification, spaceFailures[0].Message, failures: spaceFailures)
            : outcome;
    }

    /// <summary>
    /// Plans the revert flow of design 5.3: back up T (it is the live save), copy it to a new
    /// folder beside S, remove the link, and rename the copy to S. T is left for the other devices.
    /// </summary>
    public static PlanOutcome PlanRevert(Snapshot snapshot, bool assumeClosed = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var s = snapshot;
        var failures = new List<PrecheckFailure>();
        void Fail(PrecheckKind kind, Note message) => failures.Add(new PrecheckFailure(kind, message));

        if (!s.SyncRootInitialized)
        {
            Fail(PrecheckKind.SyncRootNotInitialized, SyncRootMessage(s));
        }
        else
        {
            if (s.JournalOpen)
            {
                Fail(PrecheckKind.UnfinishedOperation, UnfinishedMessage);
            }

            var isLink = s.SourceKind is EntryKind.Symlink or EntryKind.Junction;
            if (!isLink)
            {
                Fail(PrecheckKind.NotApplied, $"'{s.Source}' is not linked to the shared folder, so there is nothing to revert.");
            }
            else if (s.SourceLinkTarget is not { } linkTarget || !PathRules.AreSame(linkTarget, s.ResolvedTarget))
            {
                Fail(
                    PrecheckKind.NotApplied,
                    $"'{s.Source}' is a link to '{s.SourceLinkTarget}', not to this mapping's shared folder. SaveTether will not touch it.");
            }
            else if (s.TargetKind != EntryKind.Directory)
            {
                Fail(
                    PrecheckKind.TargetUnusable,
                    $"'{s.Source}' is linked to '{s.Target}', but that folder is missing, so the saves cannot be copied back. " +
                    "Check that the sync root is present and has finished syncing.");
            }

            if (s.RunningProcesses.Count > 0 && !assumeClosed)
            {
                failures.Add(AppRunning(s));
            }

            if (s.BackupRootProblem is { } problem)
            {
                Fail(PrecheckKind.BackupLocationUnusable, problem);
            }
        }

        if (failures.Count > 0)
        {
            return Refuse(
                s, RefusalKind.Precondition, Classification.NotClassified,
                failures.Count == 1 ? failures[0].Message : $"{failures.Count} checks failed. Nothing was changed.",
                failures: failures, operation: PlanOperation.Revert);
        }

        var staging = s.Source + StagingSuffix;
        var steps = new List<PlanStep>();
        var notes = new List<Note> { RevertLeavesSharedNote };
        if (s.TargetTree.HasData)
        {
            steps.Add(new PlanStep(StepKind.BackUpTarget, s.Target));
        }
        else
        {
            notes.Add($"The shared folder holds no saves, so '{s.Source}' will be an empty folder.");
        }

        steps.Add(new PlanStep(StepKind.CopyToStaging, s.Target, staging));
        steps.Add(new PlanStep(StepKind.RemoveLink, s.Source, s.Target));
        steps.Add(new PlanStep(StepKind.RenameIntoPlace, staging, s.Source));
        steps.Add(new PlanStep(StepKind.WriteState, s.Source, s.Target));

        var plan = new Plan(
            s.MappingId, Classification.AlreadyApplied, null, s.Source, s.Target, steps, notes,
            NeedsFirstSeedConfirmation: false, PlanOperation.Revert);
        return SpaceFailures(s, plan) is { Count: > 0 } spaceFailures
            ? Refuse(
                s, RefusalKind.Precondition, Classification.NotClassified, spaceFailures[0].Message,
                failures: spaceFailures, operation: PlanOperation.Revert)
            : new PlanOutcome(plan, null);
    }

    /// <summary>The classification table of design 5.3. Rows are checked top to bottom; the first match wins.</summary>
    public static Classification Classify(Snapshot snapshot, bool assumeSynced = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // Rows 1 and 2: S is already a link.
        if (snapshot.SourceKind is EntryKind.Symlink or EntryKind.Junction)
        {
            var pointsAtTarget = snapshot.SourceLinkTarget is { } linkTarget
                && PathRules.AreSame(linkTarget, snapshot.ResolvedTarget);
            if (!pointsAtTarget)
            {
                return Classification.ForeignLink;
            }

            return snapshot.TargetKind == EntryKind.Directory ? Classification.AlreadyApplied : Classification.BrokenLink;
        }

        // Row 3: someone seeded this mapping, yet nothing is here. Never a normal later state.
        var targetHasData = snapshot.TargetTree.HasData;
        if (!targetHasData && snapshot.ActiveMarkers.Count > 0 && !assumeSynced)
        {
            return Classification.NotArrived;
        }

        // Row 4: nothing local to decide about.
        if (!snapshot.SourceTree.HasData)
        {
            return Classification.LinkOnly;
        }

        // Rows 5 and 6.
        return targetHasData ? Classification.Guard : Classification.FirstDevice;
    }

    private static List<PrecheckFailure> StructuralFailures(Snapshot s, ApplyRequest request)
    {
        var failures = new List<PrecheckFailure>();
        void Fail(PrecheckKind kind, Note message) => failures.Add(new PrecheckFailure(kind, message));

        if (!s.SyncRootInitialized)
        {
            // Nothing else can be judged without the sync root.
            Fail(PrecheckKind.SyncRootNotInitialized, SyncRootMessage(s));
            return failures;
        }

        if (s.JournalOpen)
        {
            Fail(PrecheckKind.UnfinishedOperation, UnfinishedMessage);
        }

        var metaRoot = Path.Combine(s.DataRoot, Storage.SharedStore.MetaFolderName);
        if (!PathRules.IsSameOrInside(s.ResolvedTarget, s.DataRoot)
            || PathRules.AreSame(s.ResolvedTarget, s.DataRoot)
            || PathRules.IsSameOrInside(s.ResolvedTarget, metaRoot))
        {
            Fail(
                PrecheckKind.TargetOutsideDataRoot,
                $"The shared folder '{s.Target}' is not a folder inside '{s.DataRoot}'. A link in its path may lead elsewhere.");
        }

        if (PathRules.IsSameOrInside(s.ResolvedSource, s.SyncRoot))
        {
            Fail(
                PrecheckKind.SourceInsideSyncRoot,
                $"The save folder '{s.Source}' is inside the sync root. SaveTether links a folder outside the sync root to one inside it.");
        }
        else if (PathRules.IsSameOrInside(s.ResolvedTarget, s.ResolvedSource))
        {
            Fail(PrecheckKind.PathsOverlap, $"The save folder '{s.Source}' contains the shared folder '{s.Target}'.");
        }
        else if (IsTooBroad(s.ResolvedSource))
        {
            Fail(
                PrecheckKind.SourceUnusable,
                $"'{s.Source}' is a drive root or the home folder, or holds it. Linking it would move far more than a save folder. Check the path for a typing mistake.");
        }

        if (!s.SourceParentExists)
        {
            Fail(
                PrecheckKind.SourceParentMissing,
                $"The folder that should contain '{s.Source}' does not exist. Check the path for a typing mistake, and that the app is installed.");
        }

        if (s.SourceKind is EntryKind.File or EntryKind.OtherReparsePoint)
        {
            Fail(PrecheckKind.SourceUnusable, $"'{s.Source}' is not a folder, so it cannot be redirected.");
        }

        if (s.TargetKind is not (EntryKind.Missing or EntryKind.Directory))
        {
            Fail(PrecheckKind.TargetUnusable, $"The shared folder '{s.Target}' exists but is not a plain folder.");
        }

        // A link to a share is a symlink (design 5.16); an existing link is only verified, so it needs no privilege.
        if (!s.TargetOnLocalVolume && s.SourceKind is not (EntryKind.Symlink or EntryKind.Junction))
        {
            if (s.ShareLink == ShareLinkSupport.PolicyOff)
            {
                Fail(PrecheckKind.TargetNotLocal, $"The shared folder '{s.Target}' is on a network share. {SymlinkCapability.PolicyOffReason}");
            }
            else if (s.ShareLink == ShareLinkSupport.NeedsDevModeOrElevation)
            {
                Fail(PrecheckKind.SymlinkNotAllowed, $"The shared folder '{s.Target}' is on a network share. {SymlinkCapability.DeveloperModeFix}");
            }
        }

        if (!s.SourceOnLocalVolume)
        {
            Fail(
                PrecheckKind.SourceNotLocal,
                $"The folder '{s.Source}' is on a network drive. On Windows SaveTether can only link a folder on a local drive, because a junction cannot be made on a network drive.");
        }

        if (s.Sandbox is { CanReach: false } sandbox)
        {
            Fail(
                PrecheckKind.SandboxCannotReach,
                new Note(
                    $"The save folder is inside the Flatpak app {sandbox.AppId}, whose sandbox cannot write {string.Join(" or ", sandbox.Unreachable.Select(p => $"'{p}'"))}, " +
                    $"so the link would not work inside the app. SaveTether can allow it (only that folder, for this user), or run:\n  {sandbox.FixCommand}\nthen apply again.",
                    new NextStep(NextStepKind.GrantSandbox, s.MappingId)));
        }

        // An existing link is only verified and recorded, so a running app and backups do not matter.
        var sourceIsLink = s.SourceKind is EntryKind.Symlink or EntryKind.Junction;
        if (!sourceIsLink)
        {
            if (s.RunningProcesses.Count > 0 && !request.AssumeClosed)
            {
                failures.Add(AppRunning(s));
            }

            if (s.BackupRootProblem is { } problem)
            {
                Fail(PrecheckKind.BackupLocationUnusable, problem);
            }
        }

        return failures;
    }

    internal static readonly Note UnfinishedMessage = new("An earlier operation did not finish.", new NextStep(NextStepKind.Recover));

    internal static PrecheckFailure AppRunning(IReadOnlyList<string> running, IReadOnlyList<string> lookedFor) =>
        new(
            PrecheckKind.AppRunning,
            $"The app appears to be running ({string.Join(", ", running)}). Close it and try again. " +
            $"Looked for: {string.Join(", ", lookedFor)}.")
        {
            Processes = new ProcessFacts(running, lookedFor),
        };

    /// <param name="what">What needs the space, as the start of the sentence: "The backup", "Restoring".</param>
    internal static PrecheckFailure NotEnoughSpace(string what, string path, long needed, long free) =>
        new(PrecheckKind.NotEnoughSpace, $"{what} needs {needed:N0} bytes, but only {free:N0} are free at '{path}'.")
        {
            Space = new SpaceFacts(path, needed, free),
        };

    internal static string SyncRootMessage(string syncRoot) =>
        $"'{syncRoot}' is not set up for SaveTether, or is not available. " +
        "If the sync root is on a removable drive, check that it is connected; otherwise wait for the first sync to finish.";

    private static PrecheckFailure AppRunning(Snapshot s) => AppRunning(s.RunningProcesses, s.ProcessNamesChecked);

    private static string SyncRootMessage(Snapshot s) => SyncRootMessage(s.SyncRoot);

    private static List<PrecheckFailure> SpaceFailures(Snapshot s, Plan plan)
    {
        long backupBytes = 0;
        long targetBytes = 0;
        long stagingBytes = 0;
        foreach (var step in plan.Steps)
        {
            switch (step.Kind)
            {
                case StepKind.BackUpSource:
                    backupBytes += s.SourceTree.TotalBytes;
                    break;
                case StepKind.BackUpTarget:
                    backupBytes += s.TargetTree.TotalBytes;
                    break;
                case StepKind.CopyIntoTarget:
                    targetBytes += step.Entries is null
                        ? s.SourceTree.TotalBytes
                        : s.SourceTree.Entries
                            .Where(e => step.Entries.Any(entry => TreeFacts.IsUnder(e.RelativePath, entry)))
                            .Sum(e => e.Length);
                    break;
                case StepKind.CopyToStaging:
                    stagingBytes += s.TargetTree.TotalBytes;
                    break;
            }
        }

        var failures = new List<PrecheckFailure>();
        if (s.FreeBytesAtSource is { } sourceFree && stagingBytes > sourceFree)
        {
            failures.Add(NotEnoughSpace("Copying the saves back", s.Source, stagingBytes, sourceFree));
        }

        if (s.FreeBytesAtBackupRoot is { } backupFree && backupBytes > backupFree)
        {
            failures.Add(NotEnoughSpace("The backup", s.BackupRoot, backupBytes, backupFree));
        }

        if (s.FreeBytesAtTarget is { } targetFree && targetBytes > targetFree)
        {
            failures.Add(NotEnoughSpace("Copying the saves", s.Target, targetBytes, targetFree));
        }

        return failures;
    }

    private static PlanOutcome PlanAlreadyApplied(Snapshot s, ApplyRequest request)
    {
        // The way out of a double seed (design 5.3). This device is already linked, so there is nothing
        // to copy or delete: the shared folder as it stands is declared the winner, by renewing this
        // device's marker and superseding the others.
        if (request.Choice == SetupChoice.ReplaceShared && OthersHaveMarkers(s))
        {
            return Planned(
                s, Classification.AlreadyApplied, SetupChoice.ReplaceShared,
                [
                    new PlanStep(StepKind.VerifyLink, s.Source, s.Target),
                    new PlanStep(StepKind.WriteSeedMarker, s.Target),
                    new PlanStep(StepKind.SupersedeMarkers, s.Target),
                    new PlanStep(StepKind.WriteState, s.Source, s.Target),
                ],
                [
                    AlreadyAppliedReplaceNote,
                    "If the shared folder does not hold the saves you want: revert, restore this device's backup, " +
                    "then apply again, replacing the shared saves.",
                ]);
        }

        return Planned(
            s, Classification.AlreadyApplied, choice: null,
            [
                new PlanStep(StepKind.VerifyLink, s.Source, s.Target),
                new PlanStep(StepKind.WriteState, s.Source, s.Target),
            ],
            ["Already linked. Nothing to change; the link is checked and recorded."]);
    }

    private static PlanOutcome PlanLinkOnly(Snapshot s, ApplyRequest request)
    {
        if (request.Choice == SetupChoice.ReplaceShared)
        {
            return Refuse(
                s, RefusalKind.ChoiceNotAllowed, Classification.LinkOnly,
                new Note(
                    "This device has no saves in that folder, so there is nothing to replace the shared copy with.",
                    new NextStep(NextStepKind.ApplyWithoutChoice, s.MappingId)));
        }

        var notes = new List<string>();
        if (request.Choice is not null)
        {
            notes.Add("This device has no saves in that folder, so there is nothing to copy; it will just be linked.");
        }

        AddArrivalNote(s, notes);
        var steps = new List<PlanStep> { new(StepKind.EnsureTarget, s.Target) };
        AddSwitchSteps(s, steps);
        return Planned(s, Classification.LinkOnly, SetupChoice.NoCopy, steps, notes);
    }

    private static PlanOutcome PlanFirstDevice(Snapshot s, ApplyRequest request)
    {
        switch (request.Choice)
        {
            case null:
                return Refuse(
                    s, RefusalKind.ChoiceNeeded, Classification.FirstDevice,
                    new Note(
                        "This device has saves in that folder and the shared location is empty.",
                        new NextStep(NextStepKind.ChooseSetup, s.MappingId, Choices: FirstDeviceChoices)),
                    allowed: FirstDeviceChoices, comparison: Compare(s));

            case SetupChoice.Copy:
            {
                var steps = new List<PlanStep>
                {
                    new(StepKind.BackUpSource, s.Source),
                    new(StepKind.EnsureTarget, s.Target),
                    new(StepKind.CopyIntoTarget, s.Source, s.Target),
                };
                AddSwitchSteps(s, steps, StepKind.WriteSeedMarker, OthersHaveMarkers(s) ? StepKind.SupersedeMarkers : null);
                return Planned(
                    s, Classification.FirstDevice, SetupChoice.Copy, steps, [],
                    needsFirstSeedConfirmation: s.ActiveMarkers.Count == 0);
            }

            case SetupChoice.NoCopy:
            {
                var steps = new List<PlanStep> { new(StepKind.BackUpSource, s.Source), new(StepKind.EnsureTarget, s.Target) };
                AddSwitchSteps(s, steps);
                return Planned(
                    s, Classification.FirstDevice, SetupChoice.NoCopy, steps,
                    ["The shared location is empty, so the app will see no data after this. This device's data will be in the backup only."]);
            }

            default:
                return Refuse(
                    s, RefusalKind.ChoiceNotAllowed, Classification.FirstDevice,
                    new Note(
                        "That choice is for when the shared location already holds saves, and it is empty.",
                        new NextStep(NextStepKind.UseAllowedChoice, s.MappingId, Choices: FirstDeviceChoices)),
                    allowed: FirstDeviceChoices, comparison: Compare(s));
        }
    }

    private static PlanOutcome PlanGuard(Snapshot s, ApplyRequest request)
    {
        var comparison = Compare(s);
        var maybeArriving = s.MissingSeedFiles.Count > 0;

        switch (request.Choice)
        {
            case null:
                return Refuse(
                    s, RefusalKind.ChoiceNeeded, Classification.Guard,
                    new Note(
                        "The shared location already holds saves, and so does this device.",
                        new NextStep(NextStepKind.ChooseSetup, s.MappingId, Choices: GuardChoices)),
                    allowed: GuardChoices, comparison: comparison);

            case SetupChoice.Copy:
                return Refuse(
                    s, RefusalKind.ChoiceNotAllowed, Classification.Guard,
                    new Note(
                        "The shared location already holds saves, so making this device's saves the shared copy is refused: it would overwrite them.",
                        new NextStep(NextStepKind.UseAllowedChoice, s.MappingId, Choices: GuardChoices)),
                    allowed: GuardChoices, comparison: comparison);

            case SetupChoice.NoCopy:
            {
                var notes = new List<string>();
                AddArrivalNote(s, notes);
                var steps = new List<PlanStep> { new(StepKind.BackUpSource, s.Source) };
                AddSwitchSteps(s, steps);
                return Planned(s, Classification.Guard, SetupChoice.NoCopy, steps, notes);
            }

            case SetupChoice.AddMissing:
            {
                if (maybeArriving && !request.AssumeSynced)
                {
                    return RefuseMaybeArriving(s, comparison);
                }

                var missing = comparison.OnlyLocalEntries;
                var kept = comparison.Entries.Where(e => e.Presence == EntryPresence.Different).Select(e => e.Name).ToList();
                var notes = new List<string>
                {
                    missing.Count == 0
                        ? "The shared copy already has every entry this device has. Nothing will be copied."
                        : $"Will add {missing.Count} entr{(missing.Count == 1 ? "y" : "ies")} the shared copy lacks: {string.Join(", ", missing)}.",
                };
                if (kept.Count > 0)
                {
                    notes.Add($"Present on both sides and different; the shared version is kept, this device's is in the backup: {string.Join(", ", kept)}.");
                }

                var steps = new List<PlanStep> { new(StepKind.BackUpSource, s.Source) };
                if (missing.Count > 0)
                {
                    steps.Add(new PlanStep(StepKind.CopyIntoTarget, s.Source, s.Target, missing));
                }

                AddSwitchSteps(s, steps);
                return Planned(s, Classification.Guard, SetupChoice.AddMissing, steps, notes);
            }

            default: // ReplaceShared
            {
                if (!request.ConfirmReplace)
                {
                    return Refuse(
                        s, RefusalKind.ConfirmationNeeded, Classification.Guard,
                        new Note(
                            "Replacing the shared saves deletes them from the sync folder, which other devices may use. " +
                            "The shared copy is backed up on this device first.",
                            new NextStep(NextStepKind.ConfirmReplace, s.MappingId)),
                        allowed: GuardChoices, comparison: comparison);
                }

                if (maybeArriving && !request.AssumeSynced)
                {
                    return RefuseMaybeArriving(s, comparison);
                }

                var steps = new List<PlanStep>
                {
                    new(StepKind.BackUpSource, s.Source),
                    new(StepKind.BackUpTarget, s.Target),
                    new(StepKind.ClearTarget, s.Target),
                    new(StepKind.CopyIntoTarget, s.Source, s.Target),
                };
                AddSwitchSteps(s, steps, StepKind.WriteSeedMarker, StepKind.SupersedeMarkers);
                return Planned(
                    s, Classification.Guard, SetupChoice.ReplaceShared, steps,
                    [ReplaceSharedNote]);
            }
        }
    }

    /// <summary>The part every flow shares: move S aside, link, verify, record, then drop the aside copy.</summary>
    private static void AddSwitchSteps(Snapshot s, List<PlanStep> steps, params StepKind?[] afterVerify)
    {
        var sourceExists = s.SourceKind == EntryKind.Directory;
        var aside = s.Source + AsideSuffix;
        if (sourceExists)
        {
            steps.Add(new PlanStep(StepKind.RenameAside, s.Source, aside));
        }

        steps.Add(new PlanStep(StepKind.CreateLink, s.Source, s.Target));
        steps.Add(new PlanStep(StepKind.VerifyLink, s.Source, s.Target));
        steps.AddRange(afterVerify.OfType<StepKind>().Select(kind => new PlanStep(kind, s.Target)));
        steps.Add(new PlanStep(StepKind.WriteState, s.Source, s.Target));
        if (sourceExists)
        {
            steps.Add(new PlanStep(StepKind.DeleteAside, aside));
        }
    }

    /// <summary>True for a filesystem root, the user's home folder, or a folder that holds it.</summary>
    private static bool IsTooBroad(string resolvedSource)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return PathRules.AreSame(resolvedSource, Path.GetPathRoot(resolvedSource) ?? resolvedSource)
            || (home.Length > 0 && PathRules.IsSameOrInside(PathRules.ResolveAll(home), resolvedSource));
    }

    private static void AddArrivalNote(Snapshot s, List<string> notes)
    {
        if (s.MissingSeedFiles.Count > 0)
        {
            notes.Add(
                $"{s.MissingSeedFiles.Count} file(s) from the original shared copy are not here. " +
                "They may still be arriving, or may have been changed since; linking is safe either way.");
        }
    }

    private static PlanOutcome RefuseMaybeArriving(Snapshot s, Comparison comparison) => Refuse(
        s, RefusalKind.NotArrived, Classification.Guard,
        $"{s.MissingSeedFiles.Count} file(s) from the original shared copy are not here, so the shared copy may still be arriving. " +
        "Comparing against a partial copy could add or replace the wrong things. Wait for the sync to finish.",
        allowed: GuardChoices, assumeSyncedWouldHelp: true, comparison: comparison);

    /// <summary>
    /// Row 3's refusal. When the shared copy this very device made is empty and its save folder is
    /// a real folder holding files, waiting for the sync will not help. The likely cause is a move
    /// of the app's folder to another drive by a tool that followed the link: it copied the shared
    /// saves into a real folder, then deleted them through the link (PowerShell Move-Item and
    /// robocopy /MOVE do this; Explorer does not). Those copied files are the saves.
    /// </summary>
    private static Note NotArrivedMessage(Snapshot s)
    {
        var text =
            $"Device '{s.ActiveMarkers[0].DeviceName}' made its saves the shared copy, but the shared folder is empty here. " +
            "The sync has probably not finished. Wait for it, then try again.";
        var emptiedThroughLink = s.ActiveMarkers.Any(m => m.DeviceName == s.DeviceName)
            && s.SourceKind == EntryKind.Directory && s.SourceTree.HasData;
        return emptiedThroughLink
            ? new Note(
                text + Environment.NewLine +
                "This device made that shared copy, and its save folder holds files. If the app's folder was moved to another drive " +
                "with a tool that followed the link (PowerShell Move-Item or robocopy /MOVE), the shared saves were deleted through it " +
                "and the files here are the saves. Check them first.",
                new NextStep(NextStepKind.ReseedFromHere, s.MappingId))
            : text;
    }

    private static bool OthersHaveMarkers(Snapshot s) => s.ActiveMarkers.Any(m => m.DeviceName != s.DeviceName);

    private static Comparison Compare(Snapshot s) => Comparison.Build(s.SourceTree, s.TargetTree, s.EntryDepth);

    private static PlanOutcome Planned(
        Snapshot s, Classification classification, SetupChoice? choice, IReadOnlyList<PlanStep> steps,
        IReadOnlyList<string> notes, bool needsFirstSeedConfirmation = false)
    {
        var allNotes = notes.Select(n => new Note(n)).ToList();
        if (s.Sandbox is { CanReach: null } unknown)
        {
            allNotes.Add(
                $"The save folder is inside the Flatpak app {unknown.AppId}, but its permissions could not be read, so SaveTether " +
                "could not check that the app can reach the shared folder. Check with 'flatpak info --show-permissions " + unknown.AppId + "'.");
        }

        if (s.ActiveMarkers.Count > 1)
        {
            allNotes.Add(
                "More than one device has made its saves the shared copy for this mapping " +
                $"({string.Join(", ", s.ActiveMarkers.Select(m => m.DeviceName))}). Check which should win.");
        }

        return new PlanOutcome(
            new Plan(s.MappingId, classification, choice, s.Source, s.Target, steps, allNotes, needsFirstSeedConfirmation), null);
    }

    private static PlanOutcome Refuse(
        Snapshot s, RefusalKind kind, Classification classification, Note message,
        IReadOnlyList<PrecheckFailure>? failures = null, IReadOnlyList<SetupChoice>? allowed = null,
        bool assumeSyncedWouldHelp = false, Comparison? comparison = null, PlanOperation operation = PlanOperation.Apply) =>
        new(null, new Refusal(
            s.MappingId, kind, classification, message, failures ?? [], allowed ?? [], assumeSyncedWouldHelp, comparison, operation));
}
