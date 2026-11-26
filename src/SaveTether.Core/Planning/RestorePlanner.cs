using SaveTether.Core.Backup;
using SaveTether.Core.FileSystem;
using SaveTether.Core.Model;
using SaveTether.Core.Storage;

namespace SaveTether.Core.Planning;

/// <summary>
/// Plans "backup restore": putting a backup's content back in the folder it was taken from.
/// Like the rest of this folder it only reads. A restore is a transaction of its own, and backs
/// up what it is about to overwrite (design 5.12).
///
/// Two cases, told apart by where the backup was taken from:
/// a local save folder is rebuilt beside itself and swapped in by rename; a shared folder is
/// cleared and refilled in place, which spreads to every device and so needs the same explicit
/// confirmation as replace-shared (design 5.3, invariant 2).
/// </summary>
public static class RestorePlanner
{
    /// <summary>A note the desktop app says in its own words, so it leaves it out of its confirmation.</summary>
    public const string SharedChangesNote = "The shared folder will change, and any other devices that use it will see that.";

    public static PlanOutcome Plan(
        BackupStore backups, SharedStore shared, string backupId, RestoreRequest request,
        IReadOnlyList<string> processNames, IProcessLister processes, bool journalOpen)
    {
        ArgumentNullException.ThrowIfNull(backups);
        ArgumentNullException.ThrowIfNull(shared);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(processNames);
        ArgumentNullException.ThrowIfNull(processes);

        var info = backups.List().FirstOrDefault(b => b.Id == backupId);
        if (info is null)
        {
            return Refuse(
                backupId, PrecheckKind.BackupUnusable,
                new Note($"There is no backup '{backupId}' in '{backups.Root}'.", new NextStep(NextStepKind.ListBackups)));
        }

        if (!shared.IsInitialized)
        {
            return Refuse(info.MappingId, PrecheckKind.SyncRootNotInitialized, Planner.SyncRootMessage(shared.SyncRoot));
        }

        var failures = new List<PrecheckFailure>();
        void Fail(PrecheckKind kind, Note message) => failures.Add(new PrecheckFailure(kind, message));

        if (journalOpen)
        {
            Fail(PrecheckKind.UnfinishedOperation, Planner.UnfinishedMessage);
        }

        var verification = backups.Verify(backupId);
        if (!verification.IsValid)
        {
            Fail(
                PrecheckKind.BackupUnusable,
                $"The backup '{backupId}' no longer matches its manifest, so it will not be restored. First problem: {verification.Problems[0]}");
        }

        var running = processNames.Count == 0 ? [] : ProcessCheck.FindRunning(processNames, processes.RunningProcessNames());
        if (running.Count > 0 && !request.AssumeClosed)
        {
            failures.Add(Planner.AppRunning(running, processNames));
        }

        var data = backups.DataPathOf(backupId);
        var destination = PathRules.Normalize(info.SourcePath);
        var kind = SafeFs.Probe(destination);
        var resolved = PathRules.ResolveParents(destination);
        var isShared = PathRules.IsSameOrInside(resolved, PathRules.ResolveAll(shared.SyncRoot));
        var current = kind == EntryKind.Directory ? SafeFs.EnumerateTree(destination).ToList() : [];
        var hasData = current.Any(e => e.Kind != EntryKind.Directory);

        var steps = new List<PlanStep>();
        var notes = new List<Note>();
        var links = verification.IsValid ? backups.LinksOf(backupId) : [];
        if (isShared)
        {
            var dataRoot = PathRules.ResolveAll(shared.DataRoot);
            var insideData = PathRules.IsSameOrInside(resolved, dataRoot)
                && !PathRules.AreSame(resolved, dataRoot)
                && !PathRules.IsSameOrInside(resolved, Path.Combine(dataRoot, SharedStore.MetaFolderName));
            if (!insideData || kind is not (EntryKind.Directory or EntryKind.Missing))
            {
                Fail(
                    PrecheckKind.RestoreDestinationUnusable,
                    $"The backup was taken from '{destination}', which is not a shared save folder inside '{shared.DataRoot}' now. " +
                    $"Its files are in '{data}' if you want to copy them by hand.");
            }
            else if (failures.Count == 0 && !request.ConfirmReplace)
            {
                return new PlanOutcome(null, new Refusal(
                    info.MappingId, RefusalKind.ConfirmationNeeded, Classification.NotClassified,
                    new Note(
                        $"This backup is of the shared folder '{destination}'. Restoring it replaces what is there, and any other devices that use the sync folder will see that. " +
                        (request.NoBackups ? "No backup is taken." : "The current content is backed up on this device first."),
                        new NextStep(NextStepKind.ConfirmReplace, info.MappingId, backupId)),
                    [], [], AssumeSyncedWouldHelp: false, Comparison: null, PlanOperation.Restore));
            }

            if (hasData)
            {
                steps.Add(new PlanStep(StepKind.BackUpTarget, destination));
            }

            steps.Add(new PlanStep(StepKind.EnsureTarget, destination));
            steps.Add(new PlanStep(StepKind.ClearTarget, destination));
            steps.Add(new PlanStep(StepKind.CopyIntoTarget, data, destination));
            notes.Add(SharedChangesNote);
        }
        else
        {
            var parent = Path.GetDirectoryName(destination);
            if (kind is EntryKind.Symlink or EntryKind.Junction)
            {
                Fail(
                    PrecheckKind.RestoreDestinationUnusable,
                    new Note(
                        $"'{destination}' is a link now, so restoring into it would write to wherever it points. " +
                        "Restore a backup of the shared folder instead, or make this one a real folder again first.",
                        new NextStep(NextStepKind.Revert, info.MappingId)));
            }
            else if (kind is not (EntryKind.Directory or EntryKind.Missing))
            {
                Fail(PrecheckKind.RestoreDestinationUnusable, $"'{destination}' exists but is not a folder.");
            }
            else if (parent is null || SafeFs.Probe(PathRules.ResolveAll(parent)) != EntryKind.Directory)
            {
                Fail(
                    PrecheckKind.RestoreDestinationUnusable,
                    $"The folder that should contain '{destination}' does not exist. Its files are in '{data}' if you want to copy them by hand.");
            }

            var staging = destination + Planner.StagingSuffix;
            var aside = destination + Planner.AsideSuffix;
            if (hasData)
            {
                steps.Add(new PlanStep(StepKind.BackUpSource, destination));
            }

            steps.Add(new PlanStep(StepKind.CopyToStaging, data, staging));
            if (links.Count > 0)
            {
                steps.Add(new PlanStep(StepKind.RecreateLinks, staging, backupId, links.Select(l => l.Path).ToList()));
            }

            if (kind == EntryKind.Directory)
            {
                steps.Add(new PlanStep(StepKind.RenameAside, destination, aside));
            }

            steps.Add(new PlanStep(StepKind.RenameIntoPlace, staging, destination));
            if (kind == EntryKind.Directory)
            {
                steps.Add(new PlanStep(StepKind.DeleteAside, aside));
            }
        }

        var currentBytes = current.Sum(e => e.Length);
        if (hasData && !request.NoBackups && SnapshotReader.FreeBytesAt(backups.Root) is { } backupFree && currentBytes > backupFree)
        {
            failures.Add(Planner.NotEnoughSpace("Backing up what is there now", backups.Root, currentBytes, backupFree));
        }

        if (SnapshotReader.FreeBytesAt(destination) is { } destinationFree && info.TotalBytes > destinationFree)
        {
            failures.Add(Planner.NotEnoughSpace("Restoring", destination, info.TotalBytes, destinationFree));
        }

        if (failures.Count > 0)
        {
            return new PlanOutcome(null, new Refusal(
                info.MappingId, RefusalKind.Precondition, Classification.NotClassified,
                failures.Count == 1 ? failures[0].Message : $"{failures.Count} checks failed. Nothing was changed.",
                failures, [], AssumeSyncedWouldHelp: false, Comparison: null, PlanOperation.Restore));
        }

        if (isShared && links.Count > 0)
        {
            // SaveTether never creates links inside the sync root.
            notes.Add(
                $"{links.Count} link(s) recorded in this backup will not be recreated in the shared folder: " +
                string.Join(", ", links.Select(l => $"{l.Path} -> {l.Target}")) + ".");
        }

        var outcome = new PlanOutcome(
            new Plan(
                info.MappingId, Classification.NotClassified, null, data, destination, steps, notes,
                NeedsFirstSeedConfirmation: false, PlanOperation.Restore),
            null);
        return request.NoBackups ? Planner.WithoutBackups(outcome) : outcome;
    }

    private static PlanOutcome Refuse(string mappingId, PrecheckKind kind, Note message) => new(null, new Refusal(
        mappingId, RefusalKind.Precondition, Classification.NotClassified, message,
        [new PrecheckFailure(kind, message)], [], AssumeSyncedWouldHelp: false, Comparison: null, PlanOperation.Restore));
}
