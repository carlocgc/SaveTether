using SaveTether.Core.Backup;
using SaveTether.Core.FileSystem;
using SaveTether.Core.Model;
using SaveTether.Core.Planning;
using SaveTether.Core.Storage;

namespace SaveTether.Core.Execution;

/// <summary>Where a test can interrupt a step.</summary>
public enum FaultPoint
{
    /// <summary>The step's intent is in the journal; the step has not run.</summary>
    BeforeStep,

    /// <summary>The step has run; the journal does not yet say so.</summary>
    AfterStep,
}

/// <summary>
/// A test seam for fault injection. Throwing an <see cref="IOException"/> acts as a step that
/// failed (the executor rolls back); throwing anything else acts as a crash (nothing more runs,
/// and the journal is left open for recovery).
/// </summary>
public interface IFaultHook
{
    void At(FaultPoint point, int stepIndex, PlanStep planStep);
}

public enum ExecutionStatus
{
    /// <summary>Every step ran. For recovery: the unfinished operation was carried through.</summary>
    Completed,

    /// <summary>Nothing was started and nothing was changed.</summary>
    Refused,

    /// <summary>A step failed and the operation was undone.</summary>
    RolledBack,

    /// <summary>The operation is unfinished and the journal is still open. Run recovery.</summary>
    RecoveryNeeded,

    /// <summary>Recovery found no unfinished operation.</summary>
    NothingToRecover,
}

/// <param name="Messages">What the owner should know: what was left where, and what to do next.</param>
/// <param name="Error">Why the operation did not complete, when it did not.</param>
public sealed record ExecutionResult(
    ExecutionStatus Status,
    string? MappingId,
    PlanOperation? Operation,
    IReadOnlyList<string> BackupIds,
    IReadOnlyList<string> Messages,
    string? Error = null);

/// <summary>
/// Runs a <see cref="Plan"/> step by step (design 5.3). Every change goes through
/// <see cref="SafeFs"/>, <see cref="BackupStore"/> or the stores, and every step is journalled
/// before and after, so an interrupted run can be undone or finished by <see cref="Recover"/>.
/// </summary>
public sealed partial class Executor
{
    private readonly SharedStore _shared;
    private readonly LocalStore _local;
    private readonly BackupStore? _backups;
    private readonly string _deviceName;
    private readonly TimeProvider _time;
    private readonly IFaultHook? _faults;
    private readonly SafeFs _fs;
    private readonly Journal _journal;

    /// <param name="backups">
    /// Where backups go. May be null for recovery only, which takes no backups and must still
    /// work when the backup drive is not connected.
    /// </param>
    public Executor(
        SharedStore shared, LocalStore local, BackupStore? backups, string deviceName,
        TimeProvider? time = null, IFaultHook? faults = null)
    {
        ArgumentNullException.ThrowIfNull(shared);
        ArgumentNullException.ThrowIfNull(local);
        if (!Identifiers.IsValidDeviceName(deviceName))
        {
            throw new ExecutionException($"'{deviceName}' is not a valid device name.");
        }

        _shared = shared;
        _local = local;
        _backups = backups;
        _deviceName = deviceName;
        _time = time ?? TimeProvider.System;
        _faults = faults;
        _fs = new SafeFs(shared.SyncRoot);
        _journal = Journal.For(local);
    }

    /// <summary>Runs the plan. On a failure it rolls back, where the operation can still be undone.</summary>
    public ExecutionResult Run(Plan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (Preflight(plan) is { } refusal)
        {
            return new ExecutionResult(ExecutionStatus.Refused, plan.MappingId, plan.Operation, [], [], refusal);
        }

        var tx = new Transaction(_journal.Begin(plan, _deviceName, _time.GetUtcNow()));
        try
        {
            RunSteps(tx, resuming: false);
            _journal.Close();
            return Result(ExecutionStatus.Completed, tx);
        }
        catch (Exception e) when (IsFailure(e))
        {
            return HandleFailure(tx, e);
        }
    }

    /// <summary>What stops the plan from starting at all. Nothing has been changed.</summary>
    private string? Preflight(Plan plan)
    {
        if (_journal.IsOpen)
        {
            return Planner.UnfinishedMessage;
        }

        // Invariant 5, checked again at the moment of acting.
        if (!_shared.IsInitialized)
        {
            return Planner.SyncRootMessage(_shared.SyncRoot);
        }

        foreach (var step in plan.Steps)
        {
            var mustBeFree = step.Kind is StepKind.RenameAside or StepKind.CopyToStaging ? step.OtherPath : null;
            if (mustBeFree is not null && SafeFs.Probe(mustBeFree) != EntryKind.Missing)
            {
                return $"'{mustBeFree}' is in the way. It is left over from an earlier operation, or something else made it. " +
                    "Look at what it holds, then move or delete it by hand and try again.";
            }
        }

        return null;
    }

    private void RunSteps(Transaction tx, bool resuming)
    {
        var steps = tx.Plan.Steps;
        for (var i = 0; i < steps.Count; i++)
        {
            if (tx.Entry.Steps[i].Status == StepStatus.Done)
            {
                continue;
            }

            tx.Entry = _journal.Mark(tx.Entry, i, StepStatus.Started);
            _faults?.At(FaultPoint.BeforeStep, i, steps[i]);
            var backupId = RunStep(tx, steps[i], resuming);
            _faults?.At(FaultPoint.AfterStep, i, steps[i]);
            tx.Entry = _journal.Mark(tx.Entry, i, StepStatus.Done, backupId);
        }
    }

    /// <summary>Runs one step. Returns the backup ID when the step took a backup.</summary>
    private string? RunStep(Transaction tx, PlanStep step, bool resuming)
    {
        var plan = tx.Plan;
        switch (step.Kind)
        {
            case StepKind.BackUpSource:
                return Backups.Create(
                    plan.MappingId, step.Path,
                    plan.Operation == PlanOperation.Restore ? BackupReason.PreRestore : BackupReason.PreApply).Id;

            case StepKind.BackUpTarget:
                return Backups.Create(plan.MappingId, step.Path, plan.Operation switch
                {
                    PlanOperation.Revert => BackupReason.PreRevert,
                    PlanOperation.Restore => BackupReason.PreRestore,
                    _ => BackupReason.PreApply,
                }).Id;

            case StepKind.EnsureTarget:
                RequireSharedSaveFolder(step.Path);
                Directory.CreateDirectory(step.Path);
                return null;

            case StepKind.ClearTarget:
                ClearTarget(tx, step);
                return null;

            case StepKind.CopyIntoTarget:
                CopyIntoTarget(tx, step);
                return null;

            case StepKind.CopyToStaging:
                CopyToStaging(tx, step);
                return null;

            case StepKind.RecreateLinks:
                RecreateLinks(tx, step);
                return null;

            case StepKind.RenameAside:
                // When finishing an interrupted run, an aside folder means the rename already happened.
                if (!(resuming && SafeFs.Probe(Other(step)) != EntryKind.Missing))
                {
                    _fs.RenameDirectory(step.Path, Other(step));
                }

                return null;

            case StepKind.CreateLink:
                _fs.CreateDirectoryLink(step.Path, Other(step));
                return null;

            case StepKind.VerifyLink:
                VerifyLink(step.Path, Other(step));
                return null;

            case StepKind.RemoveLink:
                if (!(resuming && !IsLink(SafeFs.Probe(step.Path))))
                {
                    RequireLinkTo(step.Path, Other(step));
                    _fs.RemoveLink(step.Path);
                }

                return null;

            case StepKind.RenameIntoPlace:
                var alreadyInPlace = resuming
                    && SafeFs.Probe(step.Path) == EntryKind.Missing
                    && SafeFs.Probe(Other(step)) == EntryKind.Directory;
                if (!alreadyInPlace)
                {
                    _fs.RenameDirectory(step.Path, Other(step));
                }

                return null;

            case StepKind.WriteSeedMarker:
                _shared.WriteSeedMarker(new SeedMarker
                {
                    Schema = SeedMarker.CurrentSchema,
                    MappingId = plan.MappingId,
                    DeviceName = _deviceName,
                    SeededUtc = _time.GetUtcNow(),
                    Files = SafeFs.EnumerateTree(plan.Target)
                        .Where(e => e.Kind == EntryKind.File)
                        .Select(e => new SeedFile(e.RelativePath, e.Length))
                        .ToList(),
                });
                return null;

            case StepKind.SupersedeMarkers:
                _shared.SupersedeOtherMarkers(plan.MappingId, _deviceName, _time.GetUtcNow());
                return null;

            case StepKind.WriteState:
                WriteState(tx);
                return null;

            case StepKind.DeleteAside:
                if (!step.Path.EndsWith(Planner.AsideSuffix, StringComparison.Ordinal))
                {
                    throw new ExecutionException($"Refusing to delete '{step.Path}': it is not a folder SaveTether renamed aside.");
                }

                _fs.DeleteTree(step.Path);
                return null;

            default:
                throw new ExecutionException($"This version of SaveTether does not know the step '{step.Kind}'.");
        }
    }

    /// <summary>
    /// Deletes what the shared folder holds. The one exception to invariant 2, so it insists on
    /// the conditions itself rather than trusting the plan: an operation that was confirmed, and
    /// a verified local backup of the shared content taken in this same run.
    /// </summary>
    private void ClearTarget(Transaction tx, PlanStep step)
    {
        var plan = tx.Plan;
        var confirmed = plan.Operation == PlanOperation.Restore
            || (plan.Operation == PlanOperation.Apply && plan.Choice == SetupChoice.ReplaceShared);
        if (!confirmed)
        {
            throw new ExecutionException($"Refusing to clear '{step.Path}': this operation is not one that may replace shared data.");
        }

        RequireSharedSaveFolder(step.Path);
        var backedUp = plan.Steps
            .Select((s, i) => (Step: s, Record: tx.Entry.Steps[i]))
            .Any(x => x.Step.Kind == StepKind.BackUpTarget
                && PathRules.AreSame(x.Step.Path, step.Path)
                && x.Record is { Status: StepStatus.Done, BackupId: not null });
        if (HasData(step.Path) && !backedUp)
        {
            throw new ExecutionException($"Refusing to clear '{step.Path}': it holds saves that were not backed up in this run.");
        }

        foreach (var entry in Directory.GetFileSystemEntries(step.Path))
        {
            _fs.DeleteTree(entry, SharedDataDeletion.ExplicitlyConfirmed);
        }
    }

    /// <summary>Copies into the shared folder. Never overwrites: what is there must not be touched (invariant 6).</summary>
    private void CopyIntoTarget(Transaction tx, PlanStep step)
    {
        var target = Other(step);
        RequireSharedSaveFolder(target);

        if (step.Entries is null)
        {
            if (HasData(target))
            {
                throw new ExecutionException($"The shared folder '{target}' is no longer empty, so nothing was copied into it. Run the command again.");
            }
        }
        else
        {
            var present = Directory.GetFileSystemEntries(target).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (step.Entries.FirstOrDefault(present.Contains) is { } clash)
            {
                throw new ExecutionException($"The shared folder '{target}' now has '{clash}', so nothing was copied into it. Run the command again.");
            }
        }

        var copy = SafeFs.CopyInto(step.Path, target, step.Entries, LinkCopyPolicy.RecordOnly);
        VerifyCopy(step.Path, target, step.Entries);
        NoteSkippedLinks(tx, copy, step.Path);
    }

    private void CopyToStaging(Transaction tx, PlanStep step)
    {
        var staging = Other(step);
        if (!staging.EndsWith(Planner.StagingSuffix, StringComparison.Ordinal))
        {
            throw new ExecutionException($"Refusing to stage a copy at '{staging}': it is not a SaveTether staging name.");
        }

        if (PathRules.IsSameOrInside(PathRules.ResolveParents(staging), PathRules.ResolveAll(_shared.SyncRoot)))
        {
            throw new ExecutionException($"Refusing to stage a copy at '{staging}': it is inside the sync root.");
        }

        var copy = SafeFs.CopyTree(step.Path, staging, LinkCopyPolicy.RecordOnly);
        VerifyCopy(step.Path, staging, null);
        NoteSkippedLinks(tx, copy, step.Path);
    }

    /// <summary>
    /// Puts back the links a backup recorded, inside the staging folder, before it is swapped in.
    /// A link that cannot be made (Windows refuses symbolic links without a privilege) is reported
    /// and the restore goes on: the files are what matter.
    /// </summary>
    private void RecreateLinks(Transaction tx, PlanStep step)
    {
        var staging = step.Path;
        if (!staging.EndsWith(Planner.StagingSuffix, StringComparison.Ordinal) || SafeFs.Probe(staging) != EntryKind.Directory)
        {
            throw new ExecutionException($"Refusing to recreate links in '{staging}': it is not a SaveTether staging folder.");
        }

        foreach (var link in Backups.LinksOf(Other(step)))
        {
            var path = Path.GetFullPath(Path.Combine([staging, .. link.Path.Split('/')]));
            if (!PathRules.IsSameOrInside(path, staging) || PathRules.AreSame(path, staging))
            {
                throw new ExecutionException($"The backup records a link at '{link.Path}', which is not inside the folder being restored.");
            }

            try
            {
                _fs.RecreateRecordedLink(path, link.Kind == "junction" ? EntryKind.Junction : EntryKind.Symlink, link.Target);
            }
            catch (Exception e) when (IsFailure(e))
            {
                tx.Messages.Add($"The link '{link.Path}' -> '{link.Target}' recorded in the backup could not be recreated: {e.Message}");
            }
        }
    }

    private static void NoteSkippedLinks(Transaction tx, CopyResult copy, string from)
    {
        if (copy.Links.Count > 0)
        {
            tx.Messages.Add(
                $"{copy.Links.Count} link(s) inside '{from}' were not copied: " +
                string.Join(", ", copy.Links.Select(l => $"{l.RelativePath} -> {l.Target}")) + ".");
        }
    }

    /// <summary>Re-reads a copy and compares every file with its original, by size and SHA-256.</summary>
    private static void VerifyCopy(string from, string to, IReadOnlyList<string>? entries)
    {
        foreach (var entry in SafeFs.EnumerateTree(from))
        {
            if (entry.Kind != EntryKind.File
                || (entries is not null && !entries.Contains(TreeFacts.TopLevelOf(entry.RelativePath), StringComparer.Ordinal)))
            {
                continue;
            }

            var copy = Path.Combine(to, entry.RelativePath);
            var matches = SafeFs.Probe(copy) == EntryKind.File
                && new FileInfo(copy).Length == entry.Length
                && FileHasher.Sha256Hex(copy) == FileHasher.Sha256Hex(Path.Combine(from, entry.RelativePath));
            if (!matches)
            {
                throw new ExecutionException(
                    $"The copy of '{entry.RelativePath}' in '{to}' does not match the original. " +
                    "The files may have changed during the copy; make sure the app is closed.");
            }
        }
    }

    /// <summary>Design 5.3, "Verified": S is a link, it points at T, and listing S shows what T holds.</summary>
    private static void VerifyLink(string source, string target)
    {
        RequireLinkTo(source, target);
        if (SafeFs.Probe(PathRules.ResolveAll(target)) != EntryKind.Directory)
        {
            throw new ExecutionException($"'{source}' links to '{target}', but that folder is missing.");
        }

        static string?[] Names(string folder) =>
            Directory.GetFileSystemEntries(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        if (!Names(source).SequenceEqual(Names(target), StringComparer.Ordinal))
        {
            throw new ExecutionException($"'{source}' links to '{target}', but does not show the same content.");
        }
    }

    private static void RequireLinkTo(string source, string target)
    {
        var kind = SafeFs.Probe(source);
        if (!IsLink(kind))
        {
            throw new ExecutionException($"'{source}' is not a link ({kind}).");
        }

        var actual = SafeFs.ReadLinkTarget(source);
        if (!PathRules.AreSame(PathRules.ResolveAll(actual), PathRules.ResolveAll(target)))
        {
            throw new ExecutionException($"'{source}' links to '{actual}', not to '{target}'.");
        }
    }

    private void WriteState(Transaction tx)
    {
        var plan = tx.Plan;
        var state = _local.ReadState();
        var mappings = new Dictionary<string, AppliedMapping>(state.Mappings, StringComparer.Ordinal);

        if (plan.Operation == PlanOperation.Revert)
        {
            mappings.Remove(plan.MappingId);
        }
        else
        {
            // An existing link that is only being recorded again keeps what is known about it.
            var known = plan.Classification == Classification.AlreadyApplied ? mappings.GetValueOrDefault(plan.MappingId) : null;
            mappings[plan.MappingId] = new AppliedMapping
            {
                Source = plan.Source,
                Target = plan.Target,
                LinkType = SafeFs.Probe(plan.Source) == EntryKind.Junction ? LinkType.Junction : LinkType.Symlink,
                SetupChoice = plan.Choice ?? known?.SetupChoice ?? SetupChoice.NoCopy,
                AppliedUtc = known?.AppliedUtc ?? _time.GetUtcNow(),
                BackupIds = [.. known?.BackupIds ?? [], .. tx.Entry.BackupIds],
            };
        }

        _local.WriteState(state with { Mappings = mappings });
    }

    /// <summary>Fails unless the path is a mapping's folder inside &lt;sync root&gt;/savetether, outside the metadata.</summary>
    private void RequireSharedSaveFolder(string path)
    {
        var resolved = PathRules.ResolveAll(path);
        var dataRoot = PathRules.ResolveAll(_shared.DataRoot);
        var ok = PathRules.IsSameOrInside(resolved, dataRoot)
            && !PathRules.AreSame(resolved, dataRoot)
            && !PathRules.IsSameOrInside(resolved, Path.Combine(dataRoot, SharedStore.MetaFolderName));
        if (!ok)
        {
            throw new ExecutionException($"'{path}' is not a shared save folder inside '{_shared.DataRoot}'.");
        }
    }

    private BackupStore Backups =>
        _backups ?? throw new ExecutionException("This operation needs the backup location, which is not available.");

    private static bool HasData(string folder) =>
        SafeFs.Probe(folder) == EntryKind.Directory && SafeFs.EnumerateTree(folder).Any(e => e.Kind != EntryKind.Directory);

    private static bool IsLink(EntryKind kind) => kind is EntryKind.Symlink or EntryKind.Junction;

    private static string Other(PlanStep step) =>
        step.OtherPath ?? throw new ExecutionException($"The step '{step.Kind}' has no second path.");

    /// <summary>A step that failed, as opposed to a bug or a simulated crash.</summary>
    private static bool IsFailure(Exception e) => e is IOException or UnauthorizedAccessException;

    private static ExecutionResult Result(ExecutionStatus status, Transaction tx, string? error = null) =>
        new(status, tx.Plan.MappingId, tx.Plan.Operation, tx.Entry.BackupIds, tx.Messages, error);

    /// <summary>The operation in progress: its journal entry as last written, and what to tell the owner.</summary>
    private sealed class Transaction(JournalFile entry)
    {
        public JournalFile Entry { get; set; } = entry;

        public Plan Plan => Entry.Plan;

        public List<string> Messages { get; } = [];
    }
}
