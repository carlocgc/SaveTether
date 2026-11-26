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
        Assert.Equal(Enum.GetValues<SetupChoice>(), ActionTexts.Choices(all, "PPSSPP", others: true).Select(c => c.Choice));

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
        var offered = ActionTexts.Choices(firstDevice, "PPSSPP", others: false);
        Assert.Equal(firstDevice.AllowedChoices, offered.Select(c => c.Choice));
        Assert.Equal(["Share this device's data", "Start empty"], offered.Select(c => c.Label));

        var guard = Planner.Plan(Snap.Guarded(), new ApplyRequest()).Refusal!;
        var guardOffered = ActionTexts.Choices(guard, "PPSSPP", others: false);
        Assert.Equal(guard.AllowedChoices, guardOffered.Select(c => c.Choice));
        Assert.DoesNotContain(guardOffered, c => c.Choice == SetupChoice.Copy);
        Assert.Equal("Use the shared copy", guardOffered.Single(c => c.Choice == SetupChoice.NoCopy).Label);
        Assert.EndsWith("This device's data is deleted.", ActionTexts.Choices(guard, "PPSSPP", others: false, noBackups: true).Single(c => c.Choice == SetupChoice.NoCopy).Sentence, StringComparison.Ordinal);

        // Other devices are named only when there are some; the backup is said once, under the choices.
        Assert.All(guardOffered, c => Assert.DoesNotContain("other device", c.Sentence, StringComparison.OrdinalIgnoreCase));
        Assert.All(guardOffered, c => Assert.DoesNotContain("backed up", c.Sentence, StringComparison.Ordinal));
        Assert.Contains(ActionTexts.OthersGetChange, ActionTexts.Choices(guard, "PPSSPP", others: true).Single(c => c.Choice == SetupChoice.ReplaceShared).Sentence, StringComparison.Ordinal);

        // The comparison's groups, each counted.
        var groups = ActionTexts.ComparisonGroups(guard.Comparison!);
        Assert.Contains(groups, g => g.Group.StartsWith("Only on this device (", StringComparison.Ordinal));
        Assert.All(groups, g => Assert.NotEmpty(g.Entries));
    }

    [Fact]
    public void A_share_without_symlink_rights_names_the_fix_and_can_be_checked_again()
    {
        var refusal = Refusal(RefusalKind.Precondition, [new PrecheckFailure(PrecheckKind.SymlinkNotAllowed, "core message")]);
        var view = ActionTexts.Describe(refusal, Role, App, sandboxGrantPossible: false);

        Assert.Contains("Developer Mode", Assert.Single(view.Sentences), StringComparison.Ordinal);
        Assert.Equal([RefusalAction.CheckAgain], view.Actions);
    }

    [Fact]
    public void A_running_app_is_named_and_can_be_checked_again_or_declared_closed()
    {
        var refusal = Planner.Plan(
            Snap.WithLocalSaves() with { ProcessNamesChecked = ["PPSSPPSDL", "PPSSPPQt"], RunningProcesses = ["PPSSPPSDL"] },
            new ApplyRequest(SetupChoice.Copy)).Refusal!;
        var view = ActionTexts.Describe(refusal, Role, App, sandboxGrantPossible: false);

        // The app is closed, not its save folder: found on the Deck as "Close test saves first".
        // The warning that a separate dialog used to give is part of the refusal now.
        Assert.Equal([$"Close {App} first. SaveTether looked for: PPSSPPSDL, PPSSPPQt. {ActionTexts.AssumeClosedWarning(App)}"], view.Sentences);
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
            // One device in the sync folder: the dialogs name no other device.
            Assert.False(session!.HasOtherDevices);

            // While the window holds it, the CLI and a second window are refused.
            Assert.Equal("operation-running", pc.Expect(CliApp.ExitRefused, "apply", Id, "--copy", "--json").Json().GetProperty("problem").GetString());
            Assert.Contains("Another SaveTether operation is running", OperationSession.Begin(local, "apply", new Processes(), null).Refused, StringComparison.Ordinal);

            var choice = session!.PlanApply(Id, new ApplyRequest()).Refusal!;
            Assert.Equal(RefusalKind.ChoiceNeeded, choice.Kind);
            var plan = session.PlanApply(Id, new ApplyRequest(SetupChoice.Copy)).Plan!;
            var view = ActionTexts.Confirmation(plan, Role, session.HasOtherDevices);
            Assert.Equal($"Link {Role} on this device to the shared folder?", view.Title);
            Assert.Contains("A backup is taken first.", view.Sentences);
            Assert.Null(view.Phrase);
            Assert.DoesNotContain(view.Sentences, s => s.Contains("another device", StringComparison.Ordinal));

            var steps = new List<StepProgress>();
            var result = session.Run(plan, new Collect(steps));
            Assert.Equal(ExecutionStatus.Completed, result.Status);
            Assert.Equal($"{Role} is linked.", ActionTexts.Result(result, Role).Title);
            Assert.Equal(plan.Steps.Count * 2, steps.Count);

            // The finished dialog keeps the plan's steps above the result; a run with no plan has none.
            var finished = ActionTexts.FinishedView(plan, result, Role);
            Assert.Equal(plan.Steps.Select(s => ActionTexts.Step(s)), finished.Steps);
            Assert.Equal($"{Role} is linked.", finished.Title);
            Assert.Contains(result.BackupIds.Select(id => $"Backup taken: {id}").First(), finished.Sentences);
            Assert.Empty(ActionTexts.FinishedView(null, result, Role, recovery: true).Steps);
            Assert.Empty(ActionTexts.FinishedView(plan, new ExecutionResult(ExecutionStatus.Refused, Id, PlanOperation.Apply, [], []), Role).Steps);
        }

        Assert.Equal(OverallCondition.AllLinked, StatusCheck.Run(local).Condition);
        Assert.Contains(RowAction.Revert, AppsPage.Row(StatusCheck.Run(local).Mappings[0]).Others);

        // Revert, in a session of its own.
        var (revert, _) = OperationSession.Begin(local, "revert", new Processes(), null);
        using (revert)
        {
            var plan = revert!.PlanRevert(Id, assumeClosed: false).Plan!;
            var view = ActionTexts.Confirmation(plan, Role, others: false);
            Assert.Equal($"Give this device its own copy of {Role}?", view.Title);

            // The core's note says what the app's own sentence says, so it is shown once.
            Assert.Contains(Planner.RevertLeavesSharedNote, plan.Notes.Select(n => n.Text));
            Assert.DoesNotContain(Planner.RevertLeavesSharedNote, view.Sentences);
            Assert.Equal(ExecutionStatus.Completed, revert.Run(plan, null).Status);
        }

        Assert.False(pc.IsLinked);
        Assert.Equal(new Dictionary<string, string> { ["GAME1/save.bin"] = "one" }, pc.LocalFiles());

        // A second device in the sync folder: now the dialogs may name other devices.
        var deck = new CliDevice(sandbox, "deck");
        pc.DeliverTo(deck, metadataOnly: true);
        deck.SetUp();
        deck.DeliverTo(pc, metadataOnly: true);
        var (two, _) = OperationSession.Begin(local, "apply", new Processes(), null);
        using (two)
        {
            Assert.True(two!.HasOtherDevices);
        }

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
