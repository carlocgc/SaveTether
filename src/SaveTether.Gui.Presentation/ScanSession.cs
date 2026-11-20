using SaveTether.Core.Detection;
using SaveTether.Core.Storage;

namespace SaveTether.Gui.Presentation;

/// <summary>
/// The apps the last scan found, kept by the window so that leaving Add app does not lose them (desktop-app-spec.md,
/// 4.2). A scan runs as the one <see cref="BackgroundTask"/>; its rows arrive from its own thread, so the
/// window listens to <see cref="Changed"/> and marshals.
/// </summary>
public sealed class ScanSession(BackgroundTasks tasks)
{
    private readonly object _gate = new();
    private readonly List<FoundApp> _found = [];
    private bool _hasRun;
    private bool _hasCompleted;
    private bool _wasCancelled;

    // While a scan replaces an earlier, finished one, its finds are kept here and the earlier list stays on show:
    // a half-done scan must not stand in for a whole one, and a cancelled scan changes nothing.
    private List<FoundApp>? _staging;

    /// <summary>Raised when a row arrives or a scan starts or ends. Possibly on the scan's thread.</summary>
    public event Action? Changed;

    public BackgroundTasks Tasks => tasks;

    /// <summary>Whether a scan has been started in this session, so page 1 starts the first one only once.</summary>
    public bool HasRun
    {
        get
        {
            lock (_gate)
            {
                return _hasRun;
            }
        }
    }

    /// <summary>True while a scan runs that will replace a finished scan's list; <see cref="Found"/> is still that list.</summary>
    public bool Replacing
    {
        get
        {
            lock (_gate)
            {
                return _staging is not null;
            }
        }
    }

    /// <summary>How many the running scan has found so far, when it is replacing an earlier list; otherwise 0.</summary>
    public int Pending
    {
        get
        {
            lock (_gate)
            {
                return _staging?.Count ?? 0;
            }
        }
    }

    /// <summary>Whether the last scan was cancelled, so the list is the one from before it.</summary>
    public bool WasCancelled
    {
        get
        {
            lock (_gate)
            {
                return _wasCancelled;
            }
        }
    }

    /// <summary>What the scans found so far: newest save first, an app with no file last.</summary>
    public IReadOnlyList<FoundApp> Found
    {
        get
        {
            lock (_gate)
            {
                return AddAppFlow.Newest(_found);
            }
        }
    }

    /// <summary>
    /// Starts a scan on its own thread. Returns false (and starts nothing) when a scan already runs; the reason is
    /// <see cref="BackgroundTasks.BlockedReason"/>. A new scan replaces the rows of the last one.
    /// </summary>
    public bool Start(AddAppFlow flow, ScanOptions options, string name = "Scanning")
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(options);
        if (tasks.TryStart(name) is not { } task)
        {
            return false;
        }

        lock (_gate)
        {
            // The first scan shows its finds as they come; a later one is shown only once it has finished.
            if (_hasCompleted)
            {
                _staging = [];
            }
            else
            {
                _found.Clear();
                _staging = null;
            }

            _hasRun = true;
            _wasCancelled = false;
        }

        Changed?.Invoke();
        _ = Task.Run(() => Run(flow, options, task));
        return true;
    }

    private void Run(AddAppFlow flow, ScanOptions options, BackgroundTask task)
    {
        try
        {
            var count = 0;
            foreach (var app in flow.FoundApps(options, task.Token))
            {
                lock (_gate)
                {
                    // One row per app: a better hit for an app already listed replaces its row.
                    var target = _staging ?? _found;
                    target.RemoveAll(f => f.App.Id == app.App.Id);
                    target.Add(app);
                    count = target.Count;
                }

                task.Report(count, null, count);
                Changed?.Invoke();
            }

            Settle(complete: !task.Token.IsCancellationRequested, cancelled: task.Token.IsCancellationRequested);
            task.Finish();
        }
        catch (OperationCanceledException)
        {
            Settle(complete: false, cancelled: true);
            task.Finish();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or StoreException)
        {
            Settle(complete: false, cancelled: false);
            task.Fail(e.Message);
        }
        catch (Exception e)
        {
            // Anything else ends the task too, so the strip never shows a scan that has stopped.
            Settle(complete: false, cancelled: false);
            task.Fail(e.Message);
        }
        finally
        {
            Changed?.Invoke();
        }
    }

    /// <summary>A finished scan's list takes the place of the earlier one; a cancelled or failed one is dropped.</summary>
    private void Settle(bool complete, bool cancelled)
    {
        lock (_gate)
        {
            if (_staging is { } staged && complete)
            {
                _found.Clear();
                _found.AddRange(staged);
            }

            _staging = null;
            _hasCompleted |= complete;
            _wasCancelled = cancelled;
        }
    }
}
