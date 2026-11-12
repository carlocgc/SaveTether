using SaveTether.Core.FileSystem;
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
    public ExecutionResult Recover()
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
                RunSteps(tx, resuming: true);
                tx.Messages.Insert(0, $"The unfinished {what} was completed.");
                _journal.Close();
                return Result(ExecutionStatus.Completed, tx);
            }

            RollBack(tx);
            tx.Messages.Insert(0, $"The unfinished {what} was undone.");
            _journal.Close();
            return Result(ExecutionStatus.RolledBack, tx);
        }
        catch (Exception e) when (IsFailure(e))
        {
            var error = $"Recovery could not finish: {e.Message} Fix the cause and run 'savetether recover' again.";
            return tx is null
                ? new ExecutionResult(ExecutionStatus.RecoveryNeeded, null, null, [], [], error)
                : Result(ExecutionStatus.RecoveryNeeded, tx, error);
        }
    }

    /// <summary>A step failed during a normal run: undo if that is still possible, otherwise leave it to recovery.</summary>
    private ExecutionResult HandleFailure(Transaction tx, Exception failure)
    {
        var what = Describe(tx.Plan);
        try
        {
            if (PastPointOfNoReturn(tx.Entry))
            {
                return Result(
                    ExecutionStatus.RecoveryNeeded, tx,
                    $"The {what} could not be finished: {failure.Message} Fix the cause and run 'savetether recover' to complete it.");
            }

            RollBack(tx);
            _journal.Close();
            return Result(ExecutionStatus.RolledBack, tx, $"The {what} failed and was undone: {failure.Message}");
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Result(
                ExecutionStatus.RecoveryNeeded, tx,
                $"The {what} failed ({failure.Message}) and could not be undone ({e.Message}). Run 'savetether recover'.");
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
            tx.Messages.Add(
                $"The shared folder '{cleared.Step.Path}' may have been partly replaced. " +
                (backup is null
                    ? "It held no saves before."
                    : $"What it held before is in backup '{backup}'; put it back with 'savetether backup restore {backup} --confirm-replace'."));
        }
        else if (Started(StepKind.CopyIntoTarget) is { } copied)
        {
            tx.Messages.Add($"Files already copied into the shared folder '{Other(copied.Step)}' were left there.");
        }

        if (tx.Entry.BackupIds is { Count: > 0 } backups)
        {
            tx.Messages.Add($"Backups taken before it stopped: {string.Join(", ", backups)}.");
        }
    }

    private static string Describe(Plan plan) => plan.Operation switch
    {
        PlanOperation.Revert => $"revert of '{plan.MappingId}'",
        PlanOperation.Restore => $"restore into '{plan.Target}'",
        _ => $"apply of '{plan.MappingId}'",
    };
}
