using SaveTether.Core.Execution;
using SaveTether.Core.Model;
using SaveTether.Core.Planning;

namespace SaveTether.Tests.Execution;

/// <summary>
/// The executor tells a front end as each step starts and finishes, so the desktop app can tick
/// off the plan's steps (desktop-app-spec.md, sections 9.3 and 13).
/// </summary>
public class ProgressTests
{
    [Fact]
    public void Each_step_is_reported_started_then_done_in_order_after_the_journal_records_it()
    {
        using var world = new ExecWorld();
        world.LocalSave("GAME1/save.bin", "one");
        var plan = ExecWorld.Planned(world.PlanApply(new ApplyRequest(SetupChoice.Copy)));
        var recorder = new Recorder(world.Journal);

        var result = world.Executor().Run(plan, recorder);

        Assert.Equal(ExecutionStatus.Completed, result.Status);
        var expected = Enumerable.Range(0, plan.Steps.Count)
            .SelectMany(i => new[] { (i, StepStatus.Started), (i, StepStatus.Done) });
        Assert.Equal(expected, recorder.Reports.Select(r => (r.Index, r.Status)));
        Assert.All(recorder.Reports, r => Assert.Equal(plan.Steps.Count, r.Count));
        Assert.All(recorder.Reports, r => Assert.Same(plan.Steps[r.Index], r.Step));

        // Each report came after the journal said the same, so what is shown never runs ahead of what is recorded.
        Assert.All(recorder.Reports, r => Assert.Equal(r.Status, r.JournalSaid));
    }

    [Fact]
    public void A_step_that_fails_is_reported_started_and_never_done()
    {
        using var world = new ExecWorld();
        world.LocalSave("GAME1/save.bin", "one");
        var plan = ExecWorld.Planned(world.PlanApply(new ApplyRequest(SetupChoice.Copy)));
        var failing = plan.Steps.ToList().FindIndex(s => s.Kind == StepKind.CreateLink);
        var recorder = new Recorder(world.Journal);

        var result = world.Executor(new FailAt(StepKind.CreateLink, FaultPoint.AfterStep)).Run(plan, recorder);

        Assert.Equal(ExecutionStatus.RolledBack, result.Status);
        var last = recorder.Reports[^1];
        Assert.Equal((failing, StepStatus.Started), (last.Index, last.Status));
        Assert.DoesNotContain(recorder.Reports, r => r.Index == failing && r.Status == StepStatus.Done);
    }

    [Fact]
    public void Recovery_that_carries_an_operation_through_reports_the_steps_already_done_first()
    {
        using var world = new ExecWorld();
        world.LocalSave("GAME1/save.bin", "one");
        var plan = ExecWorld.Planned(world.PlanApply(new ApplyRequest(SetupChoice.Copy)));
        var marker = plan.Steps.ToList().FindIndex(s => s.Kind == StepKind.WriteSeedMarker);

        // Stopped once the link is verified: past the point of no return, so recovery completes it.
        Assert.Throws<SimulatedCrashException>(() => world.Executor(new CrashAt(marker, FaultPoint.BeforeStep)).Run(plan));
        var recorder = new Recorder(world.Journal);
        var result = world.Executor().Recover(recorder);

        Assert.Equal(ExecutionStatus.Completed, result.Status);
        var expected = Enumerable.Range(0, marker).Select(i => (i, StepStatus.Done))
            .Concat(Enumerable.Range(marker, plan.Steps.Count - marker).SelectMany(i => new[] { (i, StepStatus.Started), (i, StepStatus.Done) }));
        Assert.Equal(expected, recorder.Reports.Select(r => (r.Index, r.Status)));
    }

    [Fact]
    public void Recovery_that_undoes_an_operation_reports_no_steps()
    {
        using var world = new ExecWorld();
        world.LocalSave("GAME1/save.bin", "one");
        var plan = ExecWorld.Planned(world.PlanApply(new ApplyRequest(SetupChoice.Copy)));
        Assert.Throws<SimulatedCrashException>(() => world.Executor(new CrashAt(3, FaultPoint.AfterStep)).Run(plan));
        var recorder = new Recorder(world.Journal);

        var result = world.Executor().Recover(recorder);

        Assert.Equal(ExecutionStatus.RolledBack, result.Status);
        Assert.Empty(recorder.Reports);
        Assert.Equal("one", ExecWorld.Files(world.Source)["GAME1/save.bin"]);
    }

    [Fact]
    public void A_progress_callback_that_throws_leaves_the_operation_for_recovery_and_loses_nothing()
    {
        using var world = new ExecWorld();
        world.LocalSave("GAME1/save.bin", "one");
        var plan = ExecWorld.Planned(world.PlanApply(new ApplyRequest(SetupChoice.Copy)));
        var throwing = new Throwing(afterReports: 5);

        Assert.Throws<InvalidOperationException>(() => world.Executor().Run(plan, throwing));
        Assert.True(world.Journal.IsOpen);

        var recovered = world.Executor().Recover();
        Assert.Contains(recovered.Status, new[] { ExecutionStatus.Completed, ExecutionStatus.RolledBack });
        Assert.False(world.Journal.IsOpen);
        var saves = ExecWorld.IsLink(world.Source) ? ExecWorld.Files(world.Target) : ExecWorld.Files(world.Source);
        Assert.Equal("one", saves["GAME1/save.bin"]);
        world.Sandbox.AssertCanaryIntact();
    }

    /// <summary>Records every report as it arrives, on the executor's thread, with what the journal said at that moment.</summary>
    private sealed class Recorder(Journal journal) : IProgress<StepProgress>
    {
        public List<(int Index, int Count, PlanStep Step, StepStatus Status, StepStatus? JournalSaid)> Reports { get; } = [];

        public void Report(StepProgress value)
        {
            var said = journal.TryRead()?.Steps[value.Index].Status;
            Reports.Add((value.Index, value.Count, value.Step, value.Status, said));
        }
    }

    private sealed class Throwing(int afterReports) : IProgress<StepProgress>
    {
        private int _reports;

        public void Report(StepProgress value)
        {
            if (++_reports > afterReports)
            {
                throw new InvalidOperationException("a front end's bug");
            }
        }
    }
}
