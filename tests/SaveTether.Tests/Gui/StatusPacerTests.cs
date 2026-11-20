using SaveTether.Gui.Presentation;

namespace SaveTether.Tests.Gui;

/// <summary>The footer's background status, paced so a scan that starts and ends inside the hold is still seen.</summary>
public class StatusPacerTests
{
    private sealed class Clock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    [Fact]
    public void A_scan_that_starts_and_ends_inside_the_hold_is_shown_running_before_idle()
    {
        var clock = new Clock();
        var pacer = new StatusPacer("Idle", clock);
        Assert.Null(pacer.TryNext(out _));

        // Something is on show, so the hold is running when the scan starts and ends between two draws.
        pacer.Offer("Other");
        Assert.Equal("Other", pacer.TryNext(out _));
        clock.Advance(TimeSpan.FromMilliseconds(100));
        pacer.Offer("Scanning: 3 found");
        pacer.Offer("Scanning: 9 found");
        pacer.Offer("Idle");

        Assert.Null(pacer.TryNext(out var wait));
        Assert.Equal(Pacing.StatusMinimum - TimeSpan.FromMilliseconds(100), wait);

        clock.Advance(wait);
        Assert.Equal("Scanning: 9 found", pacer.TryNext(out wait));
        Assert.Equal(Pacing.StatusMinimum, wait);
        Assert.Null(pacer.TryNext(out _));

        clock.Advance(Pacing.StatusMinimum);
        Assert.Equal("Idle", pacer.TryNext(out wait));
        Assert.Equal(TimeSpan.Zero, wait);
        Assert.Equal("Idle", pacer.Shown);
    }

    [Fact]
    public void The_pacer_is_caught_up_only_when_the_line_on_show_is_the_latest_and_none_waits()
    {
        var clock = new Clock();
        var pacer = new StatusPacer("Idle", clock);
        var advanced = 0;
        pacer.Advanced += () => advanced++;
        Assert.True(pacer.CaughtUp);

        pacer.Offer("Scanning: 2 found");
        Assert.False(pacer.CaughtUp);
        Assert.Equal("Scanning: 2 found", pacer.TryNext(out _));
        Assert.True(pacer.CaughtUp);

        pacer.Offer("Idle");
        Assert.False(pacer.CaughtUp);
        Assert.Null(pacer.TryNext(out _));
        Assert.Equal(0, advanced - 1);

        clock.Advance(Pacing.StatusMinimum);
        Assert.Equal("Idle", pacer.TryNext(out _));
        Assert.True(pacer.CaughtUp);
        Assert.Equal(2, advanced);
    }

    [Fact]
    public void Flush_ends_the_wait_and_shows_the_latest_line()
    {
        var clock = new Clock();
        var pacer = new StatusPacer("Idle", clock);
        var advanced = 0;
        pacer.Advanced += () => advanced++;
        pacer.Offer("Scanning: 2 found");
        pacer.TryNext(out _);
        pacer.Offer("Idle");
        Assert.False(pacer.CaughtUp);

        pacer.Flush();

        Assert.True(pacer.CaughtUp);
        Assert.Equal("Idle", pacer.Shown);
        Assert.Null(pacer.TryNext(out _));
        Assert.Equal(2, advanced);
    }

    [Fact]
    public void Counts_waiting_to_be_shown_collapse_to_the_latest_and_repeats_are_ignored()
    {
        var clock = new Clock();
        var pacer = new StatusPacer("Idle", clock);
        pacer.Offer("Scanning: 1 found");
        Assert.Equal("Scanning: 1 found", pacer.TryNext(out _));

        pacer.Offer("Scanning: 2 found");
        pacer.Offer("Scanning: 3 found");
        pacer.Offer("Scanning: 3 found");
        clock.Advance(Pacing.StatusMinimum);

        Assert.Equal("Scanning: 3 found", pacer.TryNext(out var wait));
        Assert.Equal(TimeSpan.Zero, wait);
        Assert.Null(pacer.TryNext(out _));
    }
}
