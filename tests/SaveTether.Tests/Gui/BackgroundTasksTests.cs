using SaveTether.Gui.Presentation;

namespace SaveTether.Tests.Gui;

/// <summary>The background task strip's registry: one scan at a time, and the strip's words.</summary>
public class BackgroundTasksTests
{
    [Fact]
    public void A_second_scan_cannot_start_while_one_runs_and_says_why()
    {
        var tasks = new BackgroundTasks();
        Assert.True(tasks.CanStartScan);
        Assert.Null(tasks.BlockedReason);

        var first = tasks.TryStart("Scanning");

        Assert.NotNull(first);
        Assert.False(tasks.CanStartScan);
        Assert.Equal("A scan is already running", tasks.BlockedReason);
        Assert.Null(tasks.TryStart("Scanning"));
        Assert.Same(first, tasks.Current);
    }

    [Fact]
    public void The_button_comes_back_on_finish()
    {
        var tasks = new BackgroundTasks();
        tasks.TryStart("Scanning")!.Finish();

        Assert.True(tasks.CanStartScan);
        Assert.Null(tasks.Current);
        Assert.NotNull(tasks.TryStart("Scanning"));
    }

    [Fact]
    public void Cancel_signals_the_task_and_the_task_ending_clears_the_strip()
    {
        var tasks = new BackgroundTasks();
        var task = tasks.TryStart("Scanning")!;

        tasks.CancelCurrent();

        Assert.True(task.Token.IsCancellationRequested);
        Assert.False(tasks.CanStartScan);
        task.Finish();
        Assert.True(tasks.CanStartScan);
        Assert.Null(tasks.Current);
    }

    [Fact]
    public void A_failure_shows_once_until_dismissed_and_allows_the_next_scan()
    {
        var tasks = new BackgroundTasks();
        var task = tasks.TryStart("Scanning")!;

        task.Fail("disk gone");

        Assert.True(tasks.CanStartScan);
        Assert.Equal(BackgroundTaskState.Failed, tasks.Current!.State);
        Assert.Equal("Scanning failed: disk gone", tasks.Current.Text);
        tasks.Dismiss();
        Assert.Null(tasks.Current);

        tasks.TryStart("Scanning")!.Fail("again");
        Assert.NotNull(tasks.TryStart("Scanning"));
        Assert.Equal(BackgroundTaskState.Running, tasks.Current!.State);
    }

    [Fact]
    public void Dismiss_leaves_a_running_task_alone()
    {
        var tasks = new BackgroundTasks();
        var task = tasks.TryStart("Scanning")!;

        tasks.Dismiss();

        Assert.Same(task, tasks.Current);
    }

    [Fact]
    public void Counts_update_the_text_and_raise_changed()
    {
        var tasks = new BackgroundTasks();
        var changes = 0;
        tasks.Changed += () => changes++;
        var task = tasks.TryStart("Scanning")!;

        task.Report(412, 8943, 3);
        Assert.Equal("Scanning: 412 of 8,943, 3 found", task.Text);

        task.Report(500, null, 4);
        Assert.Equal("Scanning: 4 found", task.Text);
        Assert.Equal(3, changes);
    }

    [Fact]
    public void A_late_report_after_the_end_changes_nothing()
    {
        var tasks = new BackgroundTasks();
        var task = tasks.TryStart("Scanning")!;
        task.Fail("x");

        task.Report(1, 2, 3);
        task.Finish();

        Assert.Equal(0, task.Found);
        Assert.Equal(BackgroundTaskState.Failed, task.State);
        Assert.Same(task, tasks.Current);
    }
}
