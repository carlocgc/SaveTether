using System.Threading.Channels;
using SaveTether.Core.Execution;

namespace SaveTether.Gui.Presentation;

/// <summary>How long the window keeps things on screen so they can be read (desktop-app-spec.md, section 9.3).</summary>
public static class Pacing
{
    /// <summary>The least time between two steps ticked in the progress list.</summary>
    public static readonly TimeSpan StepMinimum = TimeSpan.FromMilliseconds(300);

    /// <summary>How long the finished progress list stays before the result replaces it.</summary>
    public static readonly TimeSpan FinishedHold = TimeSpan.FromMilliseconds(800);

    /// <summary>How long a dialog is shown before its buttons and Escape answer.</summary>
    public static readonly TimeSpan DialogMinimum = TimeSpan.FromMilliseconds(600);
}

/// <summary>
/// The progress list's pace (section 9.3): the executor runs at its own speed and reports from its thread;
/// the window shows those reports in order, each step's tick at least <see cref="Pacing.StepMinimum"/> after
/// the one before, then holds the finished list for <see cref="Pacing.FinishedHold"/>. A run of a few
/// milliseconds otherwise flashed past unread. Only the display waits: the operation is not slowed, and
/// its journal is closed as soon as it is done.
/// </summary>
public sealed class PacedProgress : IProgress<StepProgress>
{
    private readonly Channel<StepProgress> _reports = Channel.CreateUnbounded<StepProgress>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TimeProvider _time;
    private readonly TimeSpan _stepMinimum;
    private readonly TimeSpan _hold;

    public PacedProgress(TimeProvider? time = null, TimeSpan? stepMinimum = null, TimeSpan? hold = null)
    {
        _time = time ?? TimeProvider.System;
        _stepMinimum = stepMinimum ?? Pacing.StepMinimum;
        _hold = hold ?? Pacing.FinishedHold;
    }

    /// <summary>From the executor's thread. Never blocks and never throws into it.</summary>
    public void Report(StepProgress value) => _reports.Writer.TryWrite(value);

    /// <summary>The run has ended, however it ended: no more reports come.</summary>
    public void Complete() => _reports.Writer.TryComplete();

    /// <summary>
    /// Shows each report through <paramref name="show"/>, on the caller's context, at the pace above; ends
    /// once <see cref="Complete"/> has been called, every report has been shown, and the hold has passed.
    /// </summary>
    public async Task ShowAsync(Action<StepProgress> show)
    {
        ArgumentNullException.ThrowIfNull(show);
        var lastTick = _time.GetTimestamp();
        await foreach (var report in _reports.Reader.ReadAllAsync())
        {
            if (report.Status == StepStatus.Done)
            {
                var wait = _stepMinimum - _time.GetElapsedTime(lastTick);
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, _time);
                }

                lastTick = _time.GetTimestamp();
            }

            show(report);
        }

        await Task.Delay(_hold, _time);
    }
}
