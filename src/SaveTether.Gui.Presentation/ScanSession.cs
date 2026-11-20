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
            _found.Clear();
            _hasRun = true;
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
                    _found.RemoveAll(f => f.App.Id == app.App.Id);
                    _found.Add(app);
                    count = _found.Count;
                }

                task.Report(count, null, count);
                Changed?.Invoke();
            }

            task.Finish();
        }
        catch (OperationCanceledException)
        {
            task.Finish();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or StoreException)
        {
            task.Fail(e.Message);
        }
        catch (Exception e)
        {
            // Anything else ends the task too, so the strip never shows a scan that has stopped.
            task.Fail(e.Message);
        }
        finally
        {
            Changed?.Invoke();
        }
    }
}
