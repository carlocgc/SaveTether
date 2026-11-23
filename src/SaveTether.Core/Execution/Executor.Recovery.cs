using SaveTether.Core.FileSystem;
using SaveTether.Core.Model;
using SaveTether.Core.Planning;

namespace SaveTether.Core.Execution;

/// <summary>
/// Recovery (design 5.3, "Journal"). An unfinished operation is either undone or carried
/// through, depending on how far it got. The journal says which steps were started; the disk
/// says what actually happened, and recovery always looks at the disk before it acts, because a
/// step marked "started" may have run fully, partly or not at all.
/// </summary>
public sealed partial class Executor
{
    /// <summary>Undoes or finishes the unfinished operation, if there is one, and closes the journal.</summary>
    /// <param name="progress">
    /// Told about the plan's steps when the operation is carried through: first each step already
    /// done, then each remaining step as it starts and finishes. Not called when the operation is
    /// undone, since undoing does not run the plan's steps. The same rules as for
    /// <see cref="Run"/> apply.
    /// </param>
    public ExecutionResult Recover(IProgress<StepProgress>? progress = null)
    {
        Transaction? tx = null;
        try
        {
            var entry = _journal.TryRead();
            if (entry is null)
            {
                return _journal.IsOpen
                    ? new ExecutionResult(
                        ExecutionStatus.RecoveryNeeded, null, null, [], [],
                        "The journal exists but is not a file SaveTether can read. Look at it by hand.")
                    : new ExecutionResult(ExecutionStatus.NothingToRecover, null, null, [], []);
            }

            tx = new Transaction(entry);
            var what = Describe(tx.Plan);
            if (PastPointOfNoReturn(entry))
            {
                RunSteps(tx, resuming: true, progress);
                tx.Messages.Insert(0, $"The unfinished {what} was completed.");
                _journal.Close();
                return Result(ExecutionStatus.Completed, tx);
            }

            RollBack(tx);
            tx.Messages.Insert(0, tx.LeftInSharedFolder ? $"The unfinished {what} was undone, except in the shared folder (see below)." : $"The unfinished {what} was undone.");
            _journal.Close();
            return Result(ExecutionStatus.RolledBack, tx);
        }
        catch (Exception e) when (IsFailure(e))
        {
            var error = new Note($"Recovery could not finish: {e.Message} Fix the cause first.", new NextStep(NextStepKind.Recover));
            return tx is null
                ? new ExecutionResult(ExecutionStatus.RecoveryNeeded, null, null, [], [], error)
                : Result(ExecutionStatus.RecoveryNeeded, tx, error);
        }
    }

    /// <summary>A step failed during a normal run: undo if that is still possible, otherwise leave it to recovery.</summary>
    private ExecutionResult HandleFailure(Transaction tx, Exception failure)
    {
        var what = Describe(tx.Plan);
        var diagnostic = Diagnose(tx, failure);
        try
        {
            if (PastPointOfNoReturn(tx.Entry))
            {
                return Result(
                    ExecutionStatus.RecoveryNeeded, tx,
                    new Note(
                        $"The {what} could not be finished: {failure.Message} Fix the cause first; recovering then completes it.",
                        new NextStep(NextStepKind.Recover)),
                    diagnostic);
            }

            RollBack(tx);
            _journal.Close();
            return Result(
                ExecutionStatus.RolledBack, tx,
                tx.LeftInSharedFolder
                    ? $"The {what} failed and was undone, except in the shared folder (see below): {failure.Message}"
                    : $"The {what} failed and was undone: {failure.Message}",
                diagnostic: diagnostic);
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Result(
                ExecutionStatus.RecoveryNeeded, tx,
                new Note(
                    $"The {what} failed ({failure.Message}) and could not be undone ({e.Message}).",
                    new NextStep(NextStepKind.Recover)),
                $"{diagnostic}; and then the undo failed: {e}");
        }
    }

    /// <summary>
    /// True when the operation must be finished rather than undone.
    /// Apply: once the link is verified, only bookkeeping and the aside folder remain.
    /// Revert: once the link is gone, the verified copy only needs renaming into place.
    /// Restore of a local folder: once the old folder has been moved aside.
    /// Restore of a shared folder is never finished automatically; it is reported instead.
    /// </summary>
    private static bool PastPointOfNoReturn(JournalFile entry)
    {
        var plan = entry.Plan;
        (PlanStep Step, StepStatus Status)? Find(StepKind kind)
        {
            for (var i = 0; i < plan.Steps.Count; i++)
            {
                if (plan.Steps[i].Kind == kind)
                {
                    return (plan.Steps[i], entry.Steps[i].Status);
                }
            }

            return null;
        }

        switch (plan.Operation)
        {
            case PlanOperation.Apply:
                return Find(StepKind.VerifyLink) is { Status: StepStatus.Done };

            case PlanOperation.Revert:
                return Find(StepKind.RemoveLink) is { Status: not StepStatus.Pending } remove
                    && !IsLink(SafeFs.Probe(remove.Step.Path));

            default:
                if (Find(StepKind.RenameAside) is { } aside)
                {
                    // Started, and the disk shows the rename happened (or everything after it did too).
                    var stagingLeft = Find(StepKind.CopyToStaging) is { } staging
                        && SafeFs.Probe(Other(staging.Step)) != EntryKind.Missing;
                    return aside.Status != StepStatus.Pending
                        && !(SafeFs.Probe(Other(aside.Step)) == EntryKind.Missing && stagingLeft);
                }

                return Find(StepKind.RenameIntoPlace) is { Status: not StepStatus.Pending };
        }
    }

    /// <summary>
    /// Puts S back as it was (design 5.3 state machine, "RolledBack"). Only undoes what this
    /// operation's own steps could have done. Data already written into the shared folder is
    /// left there and reported (invariant 2).
    /// </summary>
    private void RollBack(Transaction tx)
    {
        var plan = tx.Plan;
        (PlanStep Step, JournalStep Record)? Started(StepKind kind)
        {
            for (var i = 0; i < plan.Steps.Count; i++)
            {
                if (plan.Steps[i].Kind == kind && tx.Entry.Steps[i].Status != StepStatus.Pending)
                {
                    return (plan.Steps[i], tx.Entry.Steps[i]);
                }
            }

            return null;
        }

        var aside = Started(StepKind.RenameAside) is { } rename ? Other(rename.Step) : null;
        var asideExists = aside is not null && SafeFs.Probe(aside) == EntryKind.Directory;

        if (Started(StepKind.CreateLink) is { } create)
        {
            var link = create.Step.Path;
            var kind = SafeFs.Probe(link);
            if (IsLink(kind))
            {
                if (PathRules.AreSame(PathRules.ResolveAll(SafeFs.ReadLinkTarget(link)), PathRules.ResolveAll(Other(create.Step))))
                {
                    _fs.RemoveLink(link);
                }
                else
                {
                    tx.Messages.Add($"'{link}' is a link SaveTether did not make. It was left alone.");
                }
            }
            else if (kind == EntryKind.Directory && asideExists && Directory.GetFileSystemEntries(link).Length == 0)
            {
                // A junction is made in two moves on Windows; this is the empty folder from the first.
                _fs.DeleteTree(link);
            }
        }

        if (asideExists && Started(StepKind.RenameAside) is { } renamed)
        {
            if (SafeFs.Probe(renamed.Step.Path) == EntryKind.Missing)
            {
                _fs.RenameDirectory(aside!, renamed.Step.Path);
            }
            else
            {
                tx.Messages.Add(
                    $"'{aside}' holds what was in '{renamed.Step.Path}' before the operation, but '{renamed.Step.Path}' exists again, " +
                    "so it was not put back. Compare the two by hand.");
            }
        }

        if (Started(StepKind.CopyToStaging) is { } staged
            && Other(staged.Step) is var staging
            && staging.EndsWith(Planner.StagingSuffix, StringComparison.Ordinal)
            && SafeFs.Probe(staging) != EntryKind.Missing)
        {
            _fs.DeleteTree(staging);
        }

        if (Started(StepKind.ClearTarget) is { } cleared)
        {
            var backup = tx.Entry.Steps
                .Where((_, i) => plan.Steps[i].Kind == StepKind.BackUpTarget)
                .Select(s => s.BackupId)
                .FirstOrDefault(id => id is not null);
            tx.LeftInSharedFolder = true;
            var partly = $"The shared folder '{cleared.Step.Path}' may have been partly replaced. ";
            tx.Messages.Add(
                backup is null
                    ? partly + "It held no saves before."
                    : new Note(
                        partly + $"What it held before is in backup '{backup}'.",
                        new NextStep(NextStepKind.RestoreSharedBackup, tx.Plan.MappingId, backup)));
        }
        else if (Started(StepKind.CopyIntoTarget) is { } copied)
        {
            var kept = UndoCopyIntoTarget(copied.Step);
            if (kept.Count > 0)
            {
                tx.LeftInSharedFolder = true;
                tx.Messages.Add(
                    $"Files copied into the shared folder '{Other(copied.Step)}' that no longer match this device's were left there: " +
                    $"{string.Join(", ", kept.Take(5))}{(kept.Count > 5 ? $" and {kept.Count - 5} more" : string.Empty)}.");
            }
        }

        if (tx.Entry.BackupIds is { Count: > 0 } backups)
        {
            tx.Messages.Add($"Backups taken before it stopped: {string.Join(", ", backups)}.");
        }
    }

    /// <summary>
    /// Undoes a copy into the shared folder that this operation made (design 5.3, invariant 2's second exception).
    /// Each file the step copies from S is removed from T only while it is still the copy: the same path, size and
    /// content as in S. Then the folders that copy made, once empty. A first-device copy (no entries named) went
    /// into a shared folder that held nothing, so that folder, and its app folder, go too once empty. Anything that
    /// differs, or arrived from elsewhere meanwhile, stays. Returns the files of the copy that were kept.
    /// </summary>
    private List<string> UndoCopyIntoTarget(PlanStep step)
    {
        var source = step.Path;
        var target = Other(step);
        var kept = new List<string>();
        if (SafeFs.Probe(source) != EntryKind.Directory || SafeFs.Probe(target) != EntryKind.Directory)
        {
            return kept;
        }

        bool Wanted(string relative) => step.Entries is null || step.Entries.Any(entry => TreeFacts.IsUnder(relative, entry));
        var tree = SafeFs.EnumerateTree(source).Where(e => Wanted(e.RelativePath)).ToList();
        foreach (var file in tree.Where(e => e.Kind == EntryKind.File))
        {
            var copy = Path.Combine(target, file.RelativePath);
            if (SafeFs.Probe(copy) != EntryKind.File)
            {
                continue;
            }

            var same = new FileInfo(copy).Length == file.Length
                && FileHasher.Sha256Hex(copy) == FileHasher.Sha256Hex(Path.Combine(source, file.RelativePath));
            if (same)
            {
                _fs.DeleteTree(copy, SharedDataDeletion.UndoingOwnCopy);
            }
            else
            {
                kept.Add(file.RelativePath);
            }
        }

        // Folders deepest first, and only once empty: what arrived from elsewhere keeps its folder.
        var folders = tree.Where(e => e.Kind == EntryKind.Directory).Select(e => e.RelativePath)
            .Concat(step.Entries ?? [])
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(relative => relative.Count(c => c == '/'))
            .Select(relative => Path.Combine(target, relative));
        if (step.Entries is null)
        {
            folders = folders.Append(target).Append(Path.GetDirectoryName(target)!);
        }

        foreach (var folder in folders)
        {
            if (SafeFs.Probe(folder) == EntryKind.Directory
                && PathRules.IsSameOrInside(folder, _shared.DataRoot)
                && !PathRules.AreSame(folder, _shared.DataRoot)
                && !PathRules.IsSameOrInside(folder, _shared.MetaRoot)
                && Directory.GetFileSystemEntries(folder).Length == 0)
            {
                _fs.DeleteTree(folder, SharedDataDeletion.UndoingOwnCopy);
            }
        }

        return kept;
    }

    private static string Describe(Plan plan) => plan.Operation switch
    {
        PlanOperation.Revert => $"revert of '{plan.MappingId}'",
        PlanOperation.Restore => $"restore into '{plan.Target}'",
        _ => $"apply of '{plan.MappingId}'",
    };
}
