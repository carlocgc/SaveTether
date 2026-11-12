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
    public const string AsideSuffix = ".savetether-old";

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
            Classification.AlreadyApplied => PlanAlreadyApplied(snapshot),
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
                $"Device '{snapshot.ActiveMarkers[0].DeviceName}' made its saves the shared copy, but the shared folder is empty here. " +
                "The sync has probably not finished. Wait for it, then try again.",
                assumeSyncedWouldHelp: true),
            Classification.LinkOnly => PlanLinkOnly(snapshot, request),
            Classification.FirstDevice => PlanFirstDevice(snapshot, request),
            _ => PlanGuard(snapshot, request),
        };

        return outcome.Plan is { } plan && SpaceFailures(snapshot, plan) is { Count: > 0 } spaceFailures
            ? Refuse(snapshot, RefusalKind.Precondition, classification, spaceFailures[0].Message, failures: spaceFailures)
            : outcome;
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
        void Fail(PrecheckKind kind, string message) => failures.Add(new PrecheckFailure(kind, message));

        if (!s.SyncRootInitialized)
        {
            // Nothing else can be judged without the sync root.
            Fail(
                PrecheckKind.SyncRootNotInitialized,
                $"'{s.SyncRoot}' is not set up for SaveTether, or is not available. " +
                "If the sync root is on a removable drive, check that it is connected; otherwise wait for the first sync to finish.");
            return failures;
        }

        if (s.JournalOpen)
        {
            Fail(PrecheckKind.UnfinishedOperation, "An earlier operation did not finish. Run 'savetether recover' first.");
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

        if (!s.TargetOnLocalVolume)
        {
            Fail(PrecheckKind.TargetNotLocal, $"The shared folder '{s.Target}' is on a network drive. A Windows junction can only point to a local drive.");
        }

        // An existing link is only verified and recorded, so a running app and backups do not matter.
        var sourceIsLink = s.SourceKind is EntryKind.Symlink or EntryKind.Junction;
        if (!sourceIsLink)
        {
            if (s.RunningProcesses.Count > 0 && !request.AssumeClosed)
            {
                Fail(
                    PrecheckKind.AppRunning,
                    $"The app appears to be running ({string.Join(", ", s.RunningProcesses)}). Close it and try again. " +
                    $"Looked for: {string.Join(", ", s.ProcessNamesChecked)}.");
            }

            if (s.BackupRootProblem is { } problem)
            {
                Fail(PrecheckKind.BackupLocationUnusable, problem);
            }
        }

        return failures;
    }

    private static List<PrecheckFailure> SpaceFailures(Snapshot s, Plan plan)
    {
        long backupBytes = 0;
        long targetBytes = 0;
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
                            .Where(e => step.Entries.Contains(TreeFacts.TopLevelOf(e.RelativePath), StringComparer.Ordinal))
                            .Sum(e => e.Length);
                    break;
            }
        }

        var failures = new List<PrecheckFailure>();
        if (s.FreeBytesAtBackupRoot is { } backupFree && backupBytes > backupFree)
        {
            failures.Add(new PrecheckFailure(
                PrecheckKind.NotEnoughSpace,
                $"The backup needs {backupBytes:N0} bytes, but only {backupFree:N0} are free at '{s.BackupRoot}'."));
        }

        if (s.FreeBytesAtTarget is { } targetFree && targetBytes > targetFree)
        {
            failures.Add(new PrecheckFailure(
                PrecheckKind.NotEnoughSpace,
                $"Copying the saves needs {targetBytes:N0} bytes, but only {targetFree:N0} are free at '{s.Target}'."));
        }

        return failures;
    }

    private static PlanOutcome PlanAlreadyApplied(Snapshot s) => Planned(
        s, Classification.AlreadyApplied, choice: null,
        [
            new PlanStep(StepKind.VerifyLink, s.Source, s.Target),
            new PlanStep(StepKind.WriteState, s.Source, s.Target),
        ],
        ["Already linked. Nothing to change; the link is checked and recorded."]);

    private static PlanOutcome PlanLinkOnly(Snapshot s, ApplyRequest request)
    {
        if (request.Choice == SetupChoice.ReplaceShared)
        {
            return Refuse(
                s, RefusalKind.ChoiceNotAllowed, Classification.LinkOnly,
                "This device has no saves in that folder, so there is nothing to replace the shared copy with. Run apply without a choice to just link.");
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
                    "This device has saves in that folder and the shared location is empty. " +
                    "Choose whether they become the shared copy (--copy) or stay only in the backup (--no-copy).",
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
                    ["The shared location is empty, so the app will see no saves after this. This device's saves will be in the backup only."]);
            }

            default:
                return Refuse(
                    s, RefusalKind.ChoiceNotAllowed, Classification.FirstDevice,
                    "That choice is for when the shared location already holds saves, and it is empty. Use --copy or --no-copy.",
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
                    "The shared location already holds saves, and so does this device. Choose: " +
                    "--no-copy (use the shared saves; this device's stay in the backup), " +
                    "--add-missing (also add what the shared copy lacks; nothing is overwritten), or " +
                    "--replace-shared --confirm-replace (this device's saves replace the shared copy on every device).",
                    allowed: GuardChoices, comparison: comparison);

            case SetupChoice.Copy:
                return Refuse(
                    s, RefusalKind.ChoiceNotAllowed, Classification.Guard,
                    "The shared location already holds saves, so --copy is refused: it would overwrite them. " +
                    "Use --no-copy, --add-missing, or --replace-shared --confirm-replace.",
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
                        "Replacing the shared saves deletes them on every device once it syncs. " +
                        "The shared copy is backed up on this device first. Add --confirm-replace to go ahead.",
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
                    ["The shared saves will be replaced on every device once this syncs. They are backed up on this device first."]);
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
        "Comparing against a partial copy could add or replace the wrong things. Wait for the sync to finish, " +
        "or add --assume-synced if you know it has.",
        allowed: GuardChoices, assumeSyncedWouldHelp: true, comparison: comparison);

    private static bool OthersHaveMarkers(Snapshot s) => s.ActiveMarkers.Any(m => m.DeviceName != s.DeviceName);

    private static Comparison Compare(Snapshot s) => Comparison.Build(s.SourceTree, s.TargetTree);

    private static PlanOutcome Planned(
        Snapshot s, Classification classification, SetupChoice? choice, IReadOnlyList<PlanStep> steps,
        IReadOnlyList<string> notes, bool needsFirstSeedConfirmation = false)
    {
        var allNotes = notes.ToList();
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
        Snapshot s, RefusalKind kind, Classification classification, string message,
        IReadOnlyList<PrecheckFailure>? failures = null, IReadOnlyList<SetupChoice>? allowed = null,
        bool assumeSyncedWouldHelp = false, Comparison? comparison = null) =>
        new(null, new Refusal(
            s.MappingId, kind, classification, message, failures ?? [], allowed ?? [], assumeSyncedWouldHelp, comparison));
}
