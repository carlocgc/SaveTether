using SaveTether.Core.Execution;
using SaveTether.Core.Planning;
using SaveTether.Gui.Presentation;

namespace SaveTether.Tests.Gui;

/// <summary>Section 9.3: the progress list is paced for reading, without slowing the operation.</summary>
public class PacedProgressTests
{
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(800);

    private static StepProgress Report(int index, StepStatus status) =>
        new(index, 3, new PlanStep(StepKind.CreateLink, "/p"), status);

    [Fact]
    public async Task Each_tick_waits_its_minimum_after_the_last_and_the_finished_list_is_held()
    {
        var time = new SteppingTime();
        var paced = new PacedProgress(time, Step, Hold);
        var shown = new List<(int, StepStatus)>();

        // A run of no time at all: every report arrives at once, then the run ends.
        foreach (var index in new[] { 0, 1, 2 })
        {
            paced.Report(Report(index, StepStatus.Started));
            paced.Report(Report(index, StepStatus.Done));
        }

        paced.Complete();
        await paced.ShowAsync(r => shown.Add((r.Index, r.Status)));

        Assert.Equal(
            [(0, StepStatus.Started), (0, StepStatus.Done), (1, StepStatus.Started), (1, StepStatus.Done), (2, StepStatus.Started), (2, StepStatus.Done)],
            shown);
        Assert.Equal([Step, Step, Step, Hold], time.Delays);
    }

    [Fact]
    public async Task A_step_that_took_longer_than_the_minimum_is_ticked_at_once()
    {
        var time = new SteppingTime();
        var paced = new PacedProgress(time, Step, Hold);
        var shown = new List<StepStatus>();

        var showing = paced.ShowAsync(r => shown.Add(r.Status));
        paced.Report(Report(0, StepStatus.Started));
        time.Advance(TimeSpan.FromSeconds(2));
        paced.Report(Report(0, StepStatus.Done));
        paced.Complete();
        await showing;

        Assert.Equal([StepStatus.Started, StepStatus.Done], shown);
        Assert.Equal([Hold], time.Delays);
    }

    [Fact]
    public async Task A_run_with_no_steps_is_still_held_and_reporting_never_blocks_after_the_end()
    {
        var time = new SteppingTime();
        var paced = new PacedProgress(time, Step, Hold);
        paced.Complete();
        paced.Report(Report(0, StepStatus.Done));

        await paced.ShowAsync(_ => Assert.Fail("Nothing was reported before the end."));

        Assert.Equal([Hold], time.Delays);
        Assert.True(Pacing.StepMinimum > TimeSpan.Zero && Pacing.FinishedHold > TimeSpan.Zero && Pacing.DialogMinimum > TimeSpan.Zero);
    }

    /// <summary>A clock that moves only when told, or when a delay is asked for: then it records the delay and passes it at once.</summary>
    private sealed class SteppingTime : TimeProvider
    {
        private long _ticks;

        public List<TimeSpan> Delays { get; } = [];

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (Delays)
            {
                Delays.Add(dueTime);
            }

            Advance(dueTime);
            ThreadPool.QueueUserWorkItem(_ => callback(state));
            return new NoTimer();
        }

        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
