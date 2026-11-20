using System.Globalization;

namespace SaveTether.Gui.Presentation;

/// <summary>Where a background task is (desktop-app-spec.md, the background task strip).</summary>
public enum BackgroundTaskState
{
    Running,
    Failed,
}

/// <summary>
/// One running background task, for now a scan. The task's own thread reports counts and ends it with
/// <see cref="Finish"/> or <see cref="Fail"/>; the strip's Cancel calls <see cref="Cancel"/>, which signals
/// <see cref="Token"/> and leaves the ending to the task, so a cancelled scan ends as it would have finished.
/// </summary>
public sealed class BackgroundTask : IDisposable
{
    private readonly BackgroundTasks _owner;
    private readonly CancellationTokenSource _cancel = new();

    internal BackgroundTask(BackgroundTasks owner, string name)
    {
        Token = _cancel.Token;
        _owner = owner;
        Name = name;
    }

    /// <summary>What the strip calls the task, for example "Scanning".</summary>
    public string Name { get; }

    public BackgroundTaskState State { get; internal set; } = BackgroundTaskState.Running;

    /// <summary>Items looked at so far.</summary>
    public int Done { get; private set; }

    /// <summary>Items to look at in all, when known.</summary>
    public int? Total { get; private set; }

    /// <summary>Things found so far.</summary>
    public int Found { get; private set; }

    /// <summary>Why the task failed, once it has.</summary>
    public string? Error { get; internal set; }

    /// <summary>The task's work watches this; it is signalled by <see cref="Cancel"/>.</summary>
    public CancellationToken Token { get; }

    /// <summary>
    /// The strip's line: "Scanning: 412 of 8,943, 3 found" when the total is known, otherwise just "Scanning…" (a count
    /// that starts at 0 and jumps, and that counts what the page lists differently, said nothing), or the failure's message.
    /// </summary>
    public string Text => State == BackgroundTaskState.Failed
        ? $"{Name} failed: {Error}"
        : Total is { } total
            ? string.Create(CultureInfo.InvariantCulture, $"{Name}: {Done:N0} of {total:N0}, {Found:N0} found")
            : $"{Name}…";

    /// <summary>From the task's thread.</summary>
    public void Report(int done, int? total, int found)
    {
        lock (_owner.Gate)
        {
            // A finished task's handle still says Running, so also check it is the registered one.
            if (!ReferenceEquals(_owner.Current, this) || State != BackgroundTaskState.Running)
            {
                return;
            }

            Done = done;
            Total = total;
            Found = found;
        }

        _owner.Raise();
    }

    /// <summary>Asks the task to stop; it is cleared from the strip when it ends.</summary>
    public void Cancel()
    {
        try
        {
            _cancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The task has already ended.
        }
    }

    /// <summary>Releases the cancellation source; the registry calls it when the task ends.</summary>
    public void Dispose() => _cancel.Dispose();

    /// <summary>The task ended, finished or cancelled: the strip clears and the next may start.</summary>
    public void Finish() => _owner.End(this, error: null);

    /// <summary>The task failed: the strip shows <paramref name="message"/> until dismissed.</summary>
    public void Fail(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        _owner.End(this, message);
    }
}

/// <summary>
/// The registry behind the strip along the bottom of every page: what runs in the background, one task
/// at a time. A scan only reads, so none of this touches the operation lock, and an operation neither
/// blocks nor cancels a task.
/// </summary>
public sealed class BackgroundTasks
{
    /// <summary>Shown beside a disabled scan button.</summary>
    public const string ScanRunningReason = "A scan is already running";

    /// <summary>What the footer says when nothing runs in the background.</summary>
    public const string Idle = "Idle";

    private BackgroundTask? _current;

    /// <summary>
    /// The footer's line, paced so each can be read. It is fed here, in order, on every change; the window draws
    /// it, and the scan button waits for it (<see cref="StatusPacer.CaughtUp"/>), so the two never disagree.
    /// </summary>
    public StatusPacer Status { get; } = new(Idle);

    internal object Gate { get; } = new();

    /// <summary>Raised when the strip's content changes. Possibly on a task's thread: the window marshals it.</summary>
    public event Action? Changed;

    /// <summary>The running task, or a failed one until it is dismissed; null when the strip is empty.</summary>
    public BackgroundTask? Current
    {
        get
        {
            lock (Gate)
            {
                return _current;
            }
        }
    }

    /// <summary>True while a task runs. A failed task left on the strip does not count.</summary>
    public bool IsRunning => Current is { State: BackgroundTaskState.Running };

    /// <summary>Whether a scan may start now; when not, <see cref="BlockedReason"/> says why.</summary>
    public bool CanStartScan => !IsRunning;

    /// <summary>The words beside a disabled scan button, or null when a scan may start.</summary>
    public string? BlockedReason => IsRunning ? ScanRunningReason : null;

    /// <summary>
    /// Registers a task and returns it, or returns null when one is running. A failed task still on the strip is replaced.
    /// </summary>
    public BackgroundTask? TryStart(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        BackgroundTask task;
        lock (Gate)
        {
            if (_current is { State: BackgroundTaskState.Running })
            {
                return null;
            }

            task = new BackgroundTask(this, name);
            _current = task;
        }

        Raise();
        return task;
    }

    /// <summary>Cancels the running task, if any.</summary>
    public void CancelCurrent() => Current?.Cancel();

    /// <summary>Clears a failed task from the strip; a running one stays.</summary>
    public void Dismiss()
    {
        lock (Gate)
        {
            if (_current is not { State: BackgroundTaskState.Failed })
            {
                return;
            }

            _current = null;
        }

        Raise();
    }

    internal void End(BackgroundTask task, string? error)
    {
        lock (Gate)
        {
            if (!ReferenceEquals(_current, task) || task.State != BackgroundTaskState.Running)
            {
                return;
            }

            task.Dispose();
            if (error is null)
            {
                _current = null;
            }
            else
            {
                task.State = BackgroundTaskState.Failed;
                task.Error = error;
            }
        }

        Raise();
    }

    /// <summary>Ends the footer's wait (see <see cref="StatusPacer.Flush"/>) and has the window draw the latest line.</summary>
    public void SkipPacing()
    {
        Status.Flush();
        Changed?.Invoke();
    }

    internal void Raise()
    {
        // Offered under the gate, so two threads cannot hand the footer an older line after a newer one.
        lock (Gate)
        {
            Status.Offer(_current?.Text ?? Idle);
        }

        Changed?.Invoke();
    }
}
