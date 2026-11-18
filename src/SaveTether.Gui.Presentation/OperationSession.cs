using SaveTether.Core.Execution;
using SaveTether.Core.Health;
using SaveTether.Core.Operations;
using SaveTether.Core.Planning;
using SaveTether.Core.Storage;

namespace SaveTether.Gui.Presentation;

/// <summary>What a mapping's row offers as its main action (desktop-app-spec.md, section 5.1). This part covers the operations.</summary>
public enum MainAction
{
    None,

    /// <summary>Plan and run an apply: Ready to link, Not linked, Link missing.</summary>
    Apply,

    /// <summary>Plan and run a revert: Linked.</summary>
    Revert,

    /// <summary>Apply with replace-shared on a linked folder: the way out of a double seed.</summary>
    UseThisCopy,

    /// <summary>Run a health check: a shared folder that may not have synced yet.</summary>
    CheckAgain,
}

/// <summary>Section 5.1's main actions, for the states whose action is an operation or a check.</summary>
public static class MainActions
{
    public static MainAction For(MappingHealth health)
    {
        ArgumentNullException.ThrowIfNull(health);
        return (health.State, health.Reason) switch
        {
            (HealthState.Ok, _) => MainAction.Revert,
            (HealthState.NotApplied, HealthReason.NotLinkedYet) => MainAction.Apply,
            (HealthState.Replaced, HealthReason.FolderHoldsFiles or HealthReason.FolderEmpty) => MainAction.Apply,
            (HealthState.Broken, _) => MainAction.CheckAgain,
            (HealthState.DoubleSeed, _) => MainAction.UseThisCopy,
            _ => MainAction.None,
        };
    }

    public static string Label(MainAction action) => action switch
    {
        MainAction.Apply => "Apply",
        MainAction.Revert => "Revert",
        MainAction.UseThisCopy => "Use this device's copy",
        MainAction.CheckAgain => "Check again",
        _ => string.Empty,
    };
}

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

    public ExecutionResult Recover(IProgress<StepProgress>? progress) => Workspace.Recover(progress);

    public void Dispose() => _lock.Dispose();
}
