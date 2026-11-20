using SaveTether.Core.Execution;
using SaveTether.Core.Health;
using SaveTether.Core.Model;
using SaveTether.Core.Operations;
using SaveTether.Core.Planning;
using SaveTether.Core.Storage;

namespace SaveTether.Gui.Presentation;

/// <summary>
/// One operation from the window (desktop-app-spec.md, section 7): it holds the operation lock from the
/// first plan to the result, so a plan cannot go stale behind a dialog, and releases it when disposed.
/// Planning only reads; <see cref="Run"/> and <see cref="Recover"/> change things, through the core.
/// </summary>
public sealed class OperationSession : IDisposable
{
    private readonly OperationLock _lock;

    private OperationSession(OperationLock held, Workspace workspace)
    {
        _lock = held;
        Workspace = workspace;
    }

    public Workspace Workspace { get; }

    /// <summary>Takes the lock and opens the device. On a refusal, says why in the app's words; nothing is held then.</summary>
    public static (OperationSession? Session, string? Refused) Begin(
        LocalStore local, string command, IProcessLister processes, FlatpakCheck? flatpak, IFaultHook? faults = null)
    {
        ArgumentNullException.ThrowIfNull(local);
        OperationLock held;
        try
        {
            held = OperationLock.Acquire(local, "gui", command);
        }
        catch (StoreException e) when (e.Problem == StoreProblem.OperationRunning)
        {
            var holder = OperationLock.HeldByAnotherProcess(local);
            return (null, holder is null
                ? "Another SaveTether operation is running. Try again when it has finished."
                : $"Another SaveTether operation is running ({holder.Command}). Try again when it has finished.");
        }

        try
        {
            return (new OperationSession(held, Workspace.Open(local, processes, faults: faults, flatpak: flatpak)), null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            held.Dispose();
            return (null, e.Message);
        }
    }

    public PlanOutcome PlanApply(string mappingId, ApplyRequest request) => Workspace.PlanApply(mappingId, request);

    public PlanOutcome PlanRevert(string mappingId, bool assumeClosed) => Workspace.PlanRevert(mappingId, assumeClosed);

    public PlanOutcome PlanRestore(string backupId, RestoreRequest request) => Workspace.PlanRestore(backupId, request);

    /// <summary>"backup delete", after its confirmation. Refused (a <see cref="Core.Backup.BackupException"/>) while an operation is unfinished.</summary>
    public Core.Backup.BackupDeletion DeleteBackup(string backupId) => Workspace.DeleteBackup(backupId);

    /// <summary>The Flatpak grant is offered only when it is all that stands in the way and the app's permissions could be read.</summary>
    public SandboxReach? GrantableSandbox(string mappingId, Refusal refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        return refusal is { Kind: RefusalKind.Precondition, Failures: [_, ..] failures }
            && failures.All(f => f.Kind == PrecheckKind.SandboxCannotReach)
            && Workspace.CheckSandbox(mappingId) is { CanReach: false, FixCommand: not null } reach
                ? reach
                : null;
    }

    public bool GrantSandbox(SandboxReach reach) => Workspace.GrantSandbox(reach);

    /// <summary>After a revert: SaveTether's grant to a Flatpak app that no mapping here needs now (section 9.6), or null.</summary>
    public SandboxGrant? GrantNoLongerNeeded(string mappingId) => Workspace.SandboxGrantNoLongerNeeded(mappingId);

    public bool RevokeSandbox(SandboxGrant grant) => Workspace.RevokeSandbox(grant);

    public ExecutionResult Run(Plan plan, IProgress<StepProgress>? progress) => Workspace.Run(plan, progress);

    /// <summary>"Stop using here": the core's <c>map remove</c>. Its refusal (the folder is linked) is a <see cref="StoreException"/>.</summary>
    public void StopUsing(string mappingId) => Workspace.MapRemove(mappingId);

    /// <summary>
    /// Apply all's first half (section 9.4): each mapping planned with no choice, in the order given. Those
    /// that plan can all be run after one confirmation; the others need the owner, each with why.
    /// <paramref name="choice"/> is the one answer the owner gave for the mappings that were waiting on a first-device
    /// choice (<see cref="AppsPage.NeedsFirstDeviceChoice"/>); the others are planned with it too and refused as they would be.
    /// </summary>
    public (IReadOnlyList<Plan> Plans, IReadOnlyList<Refusal> NeedYou) PlanApplyAll(IEnumerable<string> mappingIds, SetupChoice? choice = null)
    {
        ArgumentNullException.ThrowIfNull(mappingIds);
        var plans = new List<Plan>();
        var needYou = new List<Refusal>();
        foreach (var id in mappingIds)
        {
            var outcome = Workspace.PlanApply(id, new ApplyRequest(Choice: choice));
            if (outcome.Plan is { } plan)
            {
                plans.Add(plan);
            }
            else
            {
                needYou.Add(outcome.Refusal!);
            }
        }

        return (plans, needYou);
    }

    public ExecutionResult Recover(IProgress<StepProgress>? progress) => Workspace.Recover(progress);

    public void Dispose() => _lock.Dispose();
}
