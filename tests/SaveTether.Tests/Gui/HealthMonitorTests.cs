using SaveTether.Core.Health;
using SaveTether.Core.Model;
using SaveTether.Core.Operations;
using SaveTether.Core.Storage;
using SaveTether.Gui.Presentation;

namespace SaveTether.Tests.Gui;

/// <summary>Section 3.1 of the desktop app spec: when the icon changes and what is notified.</summary>
public class HealthMonitorTests
{
    private static readonly OverallStatus Green = new(OverallCondition.AllLinked, null, []);

    private static readonly LockHolder Cli = new()
    {
        Schema = LockHolder.CurrentSchema, ProcessId = 1, FrontEnd = "cli", Command = "apply", StartedUtc = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void A_problem_is_notified_once_not_at_every_check()
    {
        var next = Green;
        var monitor = new HealthMonitor(() => next, () => null);

        Assert.Null(monitor.Run()!.Notification);

        next = Attention(("ryujinx.saves", "Ryujinx", HealthState.Replaced, HealthReason.FolderHoldsFiles));
        Assert.Equal("Ryujinx saves: not linked.", monitor.Run()!.Notification);
        Assert.Null(monitor.Run()!.Notification);

        // A second problem: only the new one is named.
        next = Attention(
            ("ryujinx.saves", "Ryujinx", HealthState.Replaced, HealthReason.FolderHoldsFiles),
            ("eden.save", "Eden", HealthState.Broken, HealthReason.SharedFolderMissing));
        Assert.Equal("Eden save: shared folder missing.", monitor.Run()!.Notification);

        // Green again: nothing is notified, the icon changes.
        next = Green;
        var cleared = monitor.Run()!;
        Assert.Null(cleared.Notification);
        Assert.False(cleared.Icon.IsAmber);

        // The same problem coming back is new again.
        next = Attention(("ryujinx.saves", "Ryujinx", HealthState.Replaced, HealthReason.FolderHoldsFiles));
        Assert.Equal("Ryujinx saves: not linked.", monitor.Run()!.Notification);

        // The same mapping in another amber state is a new problem.
        next = Attention(("ryujinx.saves", "Ryujinx", HealthState.Replaced, HealthReason.FolderEmpty));
        Assert.Equal("Ryujinx saves: link missing.", monitor.Run()!.Notification);
    }

    [Fact]
    public void An_amber_state_at_start_is_notified_and_a_device_wide_condition_is_one_problem()
    {
        var unplugged = new OverallStatus(
            OverallCondition.SyncRootUnavailable, null,
            Attention(("eden.save", "Eden", HealthState.Broken, HealthReason.SyncRootUnavailable)).Mappings);
        var monitor = new HealthMonitor(() => unplugged, () => null);

        Assert.Equal("Sync folder not available.", monitor.Run()!.Notification);
        Assert.Null(monitor.Run()!.Notification);
    }

    [Fact]
    public void While_another_process_holds_the_lock_the_icon_and_the_notifications_wait()
    {
        var next = Green;
        LockHolder? holder = null;
        var monitor = new HealthMonitor(() => next, () => holder);
        monitor.Run();

        // A CLI apply has renamed the folder aside for a moment.
        holder = Cli;
        next = Attention(("ryujinx.saves", "Ryujinx", HealthState.Replaced, HealthReason.FolderMissing));
        var during = monitor.Run()!;
        Assert.Same(next, during.Shown);
        Assert.False(during.Icon.IsAmber);
        Assert.Null(during.Notification);
        Assert.Same(Cli, during.LockHeldBy);

        // Done, and linked: the problem seen while it ran is never notified.
        holder = null;
        next = Green;
        Assert.Null(monitor.Run()!.Notification);
        Assert.False(monitor.Current!.IsAmber);
    }

    [Fact]
    public void Failures_keep_the_last_state_until_the_third_in_a_row()
    {
        var fail = false;
        var monitor = new HealthMonitor(() => fail ? throw new IOException("disk gone") : Green, () => null);
        monitor.Run();

        fail = true;
        for (var i = 1; i < HealthMonitor.FailuresBeforeAmber; i++)
        {
            var kept = monitor.Run()!;
            Assert.NotNull(kept.Error);
            Assert.False(kept.Icon.IsAmber);
            Assert.Null(kept.Notification);
        }

        var third = monitor.Run()!;
        Assert.Equal(OverallCondition.CheckFailed, third.Icon.Condition);
        Assert.Equal("Health check failed.", third.Notification);

        // One good check resets the count.
        fail = false;
        monitor.Run();
        fail = true;
        Assert.False(monitor.Run()!.Icon.IsAmber);
    }

    [Fact]
    public async Task A_check_already_running_is_not_started_twice()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var monitor = new HealthMonitor(
            () =>
            {
                entered.Set();
                release.Wait();
                return Green;
            },
            () => null);

        var first = Task.Run(monitor.Run);
        entered.Wait();
        Assert.Null(monitor.Run());
        release.Set();
        Assert.NotNull(await first);
        Assert.NotNull(monitor.Run());
    }

    private static OverallStatus Attention(params (string Id, string App, HealthState State, HealthReason Reason)[] rows) =>
        new(
            OverallCondition.NeedsAttention, null,
            rows.Select(r => new MappingState(
                    r.Id, r.App, r.Id.Split('.')[1], StateWords.For(r.State, r.Reason),
                    new MappingHealth(r.Id, r.State, "/s", "/t", new Note("detail"), r.Reason)))
                .ToList());
}
