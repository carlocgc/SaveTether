using SaveTether.Cli;
using SaveTether.Core.Execution;
using SaveTether.Core.Health;
using SaveTether.Core.Model;
using SaveTether.Core.Planning;
using SaveTether.Core.Storage;
using SaveTether.Gui.Presentation;
using SaveTether.Tests.Cli;
using SaveTether.Tests.Execution;
using SaveTether.Tests.FileSystem;
using SaveTether.Tests.Planning;

namespace SaveTether.Tests.Gui;

/// <summary>Sections 7 and 9 of the desktop app spec: the app's texts for the core's answers, and operations run from the window.</summary>
public class ActionTests
{
    private const string Id = CliDevice.MappingId;
    private const string Role = "PPSSPP savedata";
    private const string App = "PPSSPP";

    [Fact]
    public void Every_refusal_precondition_step_and_result_has_an_app_text()
    {
        // A new enum value without a text fails here (section 14, exhaustiveness).
        foreach (var kind in Enum.GetValues<PrecheckKind>())
        {
            Assert.False(string.IsNullOrWhiteSpace(ActionTexts.Precondition(new PrecheckFailure(kind, "core message"), Role, App)));
        }

        foreach (var kind in Enum.GetValues<RefusalKind>())
        {
            var view = ActionTexts.Describe(Refusal(kind, [new PrecheckFailure(PrecheckKind.TargetNotLocal, "core message")]), Role, App, sandboxGrantPossible: false);
            Assert.False(string.IsNullOrWhiteSpace(view.Title));
            Assert.NotEmpty(view.Sentences);
            Assert.Contains("core message", view.Details + Refusal(kind, []).Message.Text, StringComparison.Ordinal);
        }

        foreach (var kind in Enum.GetValues<StepKind>())
        {
            Assert.False(string.IsNullOrWhiteSpace(ActionTexts.Step(new PlanStep(kind, "/p"))));
        }

        foreach (var status in Enum.GetValues<ExecutionStatus>())
        {
            foreach (var recovery in new[] { false, true })
            {
                Assert.False(string.IsNullOrWhiteSpace(ActionTexts.Result(new ExecutionResult(status, Id, PlanOperation.Apply, [], []), Role, recovery).Title));
            }
        }

        // Recovery that undoes is recovery succeeding, not the operation failing again (found on the Deck).
        Assert.Equal(
            "Recovered: the unfinished apply was undone. Nothing has changed.",
            ActionTexts.Result(new ExecutionResult(ExecutionStatus.RolledBack, Id, PlanOperation.Apply, [], []), Id, recovery: true).Title);
        Assert.Equal(
            "Apply failed and was undone. Nothing has changed.",
            ActionTexts.Result(new ExecutionResult(ExecutionStatus.RolledBack, Id, PlanOperation.Apply, [], []), Role).Title);

        var all = Refusal(RefusalKind.ChoiceNeeded, []) with { AllowedChoices = Enum.GetValues<SetupChoice>() };
        Assert.Equal(Enum.GetValues<SetupChoice>(), ActionTexts.Choices(all, "PPSSPP").Select(c => c.Choice));

        HealthReason?[] reasons = [null, .. Enum.GetValues<HealthReason>()];
        foreach (var state in Enum.GetValues<HealthState>())
        {
            foreach (var reason in reasons)
            {
                var row = AppsPage.Row(Mapping(state, reason));
                Assert.False(string.IsNullOrWhiteSpace(row.Sentence));
            }
        }

        foreach (var action in Enum.GetValues<RowAction>())
        {
            Assert.False(string.IsNullOrWhiteSpace(AppsPage.Label(action)));
        }
    }

    [Fact]
    public void The_choice_dialog_offers_exactly_the_allowed_choices()
    {
        var firstDevice = Planner.Plan(Snap.WithLocalSaves(), new ApplyRequest()).Refusal!;
        Assert.Equal(RefusalKind.ChoiceNeeded, firstDevice.Kind);
        var offered = ActionTexts.Choices(firstDevice, "PPSSPP");
        Assert.Equal(firstDevice.AllowedChoices, offered.Select(c => c.Choice));
        Assert.Equal(["Share this device's data", "Start empty"], offered.Select(c => c.Label));

        var guard = Planner.Plan(Snap.Guarded(), new ApplyRequest()).Refusal!;
        var guardOffered = ActionTexts.Choices(guard, "PPSSPP");
        Assert.Equal(guard.AllowedChoices, guardOffered.Select(c => c.Choice));
        Assert.DoesNotContain(guardOffered, c => c.Choice == SetupChoice.Copy);
        Assert.Equal("Use the shared data", guardOffered.Single(c => c.Choice == SetupChoice.NoCopy).Label);

        // The comparison's groups, with the identical ones counted.
        var groups = ActionTexts.ComparisonGroups(guard.Comparison!);
        Assert.Contains(groups, g => g.Group == "Only on this device");
        Assert.All(groups, g => Assert.NotEmpty(g.Entries));
    }

    [Fact]
    public void A_running_app_is_named_and_can_be_checked_again_or_declared_closed()
    {
        var refusal = Planner.Plan(
            Snap.WithLocalSaves() with { ProcessNamesChecked = ["PPSSPPSDL", "PPSSPPQt"], RunningProcesses = ["PPSSPPSDL"] },
            new ApplyRequest(SetupChoice.Copy)).Refusal!;
        var view = ActionTexts.Describe(refusal, Role, App, sandboxGrantPossible: false);

        // The app is closed, not its save folder: found on the Deck as "Close test saves first".
        Assert.Equal([$"Close {App} first. SaveTether looked for: PPSSPPSDL, PPSSPPQt."], view.Sentences);
        Assert.Equal([RefusalAction.CheckAgain, RefusalAction.AssumeClosed], view.Actions);
        Assert.Contains("appears to be running", view.Details, StringComparison.Ordinal);

        // Not enough space, worded from its facts.
        var space = Planner.Plan(Snap.WithLocalSaves() with { FreeBytesAtTarget = 10 }, new ApplyRequest(SetupChoice.Copy)).Refusal!;
        Assert.Equal([$"Not enough free space at {Snap.Target}: 300 bytes needed, 10 bytes free."], ActionTexts.Describe(space, Role, App, false).Sentences);
    }

    [Fact]
    public void An_operation_holds_the_lock_from_plan_to_result_and_runs_through_the_core()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        pc.Save("GAME1/save.bin", "one");
        var local = new LocalStore(pc.LocalFolder);

        var (session, refused) = OperationSession.Begin(local, "apply", new Processes(), flatpak: null);
        Assert.Null(refused);
        using (session)
        {
            // While the window holds it, the CLI and a second window are refused.
            Assert.Equal("operation-running", pc.Expect(CliApp.ExitRefused, "apply", Id, "--copy", "--json").Json().GetProperty("problem").GetString());
            Assert.Contains("Another SaveTether operation is running", OperationSession.Begin(local, "apply", new Processes(), null).Refused, StringComparison.Ordinal);

            var choice = session!.PlanApply(Id, new ApplyRequest()).Refusal!;
            Assert.Equal(RefusalKind.ChoiceNeeded, choice.Kind);
            var plan = session.PlanApply(Id, new ApplyRequest(SetupChoice.Copy)).Plan!;
            var (title, sentences) = ActionTexts.Confirmation(plan, Role);
            Assert.Equal($"Link {Role} on this device to the shared folder?", title);
            Assert.Contains("A backup is taken first.", sentences);

            var steps = new List<StepProgress>();
            var result = session.Run(plan, new Collect(steps));
            Assert.Equal(ExecutionStatus.Completed, result.Status);
            Assert.Equal($"{Role} is linked.", ActionTexts.Result(result, Role).Title);
            Assert.Equal(plan.Steps.Count * 2, steps.Count);
        }

        Assert.Equal(OverallCondition.AllLinked, StatusCheck.Run(local).Condition);
        Assert.Contains(RowAction.Revert, AppsPage.Row(StatusCheck.Run(local).Mappings[0]).Others);

        // Revert, in a session of its own.
        var (revert, _) = OperationSession.Begin(local, "revert", new Processes(), null);
        using (revert)
        {
            var plan = revert!.PlanRevert(Id, assumeClosed: false).Plan!;
            Assert.Equal($"Give this device its own copy of {Role}?", ActionTexts.Confirmation(plan, Role).Title);
            Assert.Equal(ExecutionStatus.Completed, revert.Run(plan, null).Status);
        }

        Assert.False(pc.IsLinked);
        Assert.Equal(new Dictionary<string, string> { ["GAME1/save.bin"] = "one" }, pc.LocalFiles());
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void An_unfinished_operation_is_named_and_recovered_from_the_window()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        pc.Save("GAME1/save.bin", "one");
        pc.Faults = new FailAt(StepKind.WriteState, FaultPoint.BeforeStep);
        pc.Expect(CliApp.ExitRecoveryNeeded, "apply", Id, "--copy");
        pc.Faults = null;
        var local = new LocalStore(pc.LocalFolder);

        Assert.Equal(OverallCondition.Unfinished, StatusCheck.Run(local).Condition);
        Assert.Equal(
            $"Apply of {Id} did not finish. Nothing else can be done until it is recovered.",
            ActionTexts.Unfinished(Journal.For(local).TryRead()));

        var (session, _) = OperationSession.Begin(local, "recover", new Processes(), null);
        using (session)
        {
            var result = session!.Recover(null);
            Assert.Contains(result.Status, new[] { ExecutionStatus.Completed, ExecutionStatus.RolledBack });
        }

        Assert.NotEqual(OverallCondition.Unfinished, StatusCheck.Run(local).Condition);
        sandbox.AssertCanaryIntact();
    }

    private static MappingState Mapping(HealthState state, HealthReason? reason) =>
        new(Id, "PPSSPP", "savedata", StateWords.For(state, reason), new MappingHealth(Id, state, "/s", "/t", new Note("d"), reason));

    private static Refusal Refusal(RefusalKind kind, IReadOnlyList<PrecheckFailure> failures) =>
        new(Id, kind, Classification.NotClassified, new Note("core message"), failures, [], false, null);

    private sealed class Processes : IProcessLister
    {
        public IReadOnlyList<string> RunningProcessNames() => [];
    }

    /// <summary>Collects synchronously: <see cref="Progress{T}"/> would post to a thread pool.</summary>
    private sealed class Collect(List<StepProgress> into) : IProgress<StepProgress>
    {
        public void Report(StepProgress value) => into.Add(value);
    }
}
