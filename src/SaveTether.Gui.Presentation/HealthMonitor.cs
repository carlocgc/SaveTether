using SaveTether.Core.Storage;

namespace SaveTether.Gui.Presentation;

/// <summary>What one check gave, for the window, the tray icon and the notification.</summary>
/// <param name="Shown">What the window shows: this check's result.</param>
/// <param name="Icon">What the tray icon shows. It stays as it was while another process holds the operation lock.</param>
/// <param name="Notification">The sentence to notify, or null for none.</param>
/// <param name="LockHeldBy">The other process holding the operation lock, when one does.</param>
/// <param name="Error">An unexpected failure of this check. <see cref="Shown"/> is then the last good result.</param>
public sealed record CheckResult(
    OverallStatus Shown, OverallStatus Icon, string? Notification, LockHolder? LockHeldBy = null, Exception? Error = null);

/// <summary>
/// Runs checks and decides what the icon shows and what is notified (desktop-app-spec.md, sections 3 and 3.1).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A check already running is not started twice: <see cref="Run"/> returns null instead.</item>
/// <item>While another process holds the operation lock, the folder it works on may look Replaced for a
/// moment, so the result is shown but changes neither the icon nor what has been notified.</item>
/// <item>A notification is raised only when something is amber now that was not at the last accepted check,
/// so one problem is notified once, not at every check. At start, an amber state is notified once.</item>
/// <item>It says the headline ("2 apps need attention."), not each problem: the window shows the rest.</item>
/// <item>An unexpected failure keeps the last state; three in a row turn it to "Health check failed".</item>
/// </list>
/// </remarks>
public sealed class HealthMonitor(Func<OverallStatus> check, Func<LockHolder?> lockHeldByAnother)
{
    public const int FailuresBeforeAmber = 3;

    private readonly Lock _gate = new();
    private OverallStatus? _accepted;
    private HashSet<string> _notified = [];
    private int _failures;
    private bool _running;

    /// <summary>For a device on disk: the core's status, and its operation lock.</summary>
    public static HealthMonitor For(LocalStore local) =>
        new(() => StatusCheck.Run(local), () => OperationLock.HeldByAnotherProcess(local));

    /// <summary>The last result the icon shows, or null before the first check.</summary>
    public OverallStatus? Current
    {
        get
        {
            lock (_gate)
            {
                return _accepted;
            }
        }
    }

    /// <summary>Runs one check on the calling thread. Null when one is already running.</summary>
    public CheckResult? Run()
    {
        lock (_gate)
        {
            if (_running)
            {
                return null;
            }

            _running = true;
        }

        try
        {
            OverallStatus next;
            try
            {
                next = check();
            }
#pragma warning disable CA1031 // The tray must survive any failure of a background check; it is reported, not swallowed.
            catch (Exception e)
#pragma warning restore CA1031
            {
                return Failed(e);
            }

            var holder = lockHeldByAnother();
            lock (_gate)
            {
                _failures = 0;
                if (holder is not null)
                {
                    return new CheckResult(next, _accepted ?? next, null, holder);
                }

                return Accept(next);
            }
        }
        finally
        {
            lock (_gate)
            {
                _running = false;
            }
        }
    }

    private CheckResult Failed(Exception error)
    {
        lock (_gate)
        {
            _failures++;
            if (_failures >= FailuresBeforeAmber)
            {
                var failed = new OverallStatus(OverallCondition.CheckFailed, null, [], error.Message);
                var result = Accept(failed);
                return result with { Error = error };
            }

            var last = _accepted ?? new OverallStatus(OverallCondition.CheckFailed, null, [], error.Message);
            return new CheckResult(last, last, null, Error: error);
        }
    }

    /// <summary>Takes a result as the icon's state, and works out whether anything is new since the last one.</summary>
    private CheckResult Accept(OverallStatus next)
    {
        var problems = Problems(next);
        var fresh = problems.Any(p => !_notified.Contains(p));
        _notified = problems.ToHashSet(StringComparer.Ordinal);
        _accepted = next;
        return new CheckResult(next, next, fresh ? next.Headline + "." : null);
    }

    /// <summary>
    /// What is amber, each as a key that stays the same while the problem does. A device-wide condition
    /// (section 3, rows 1 to 5) is one problem: the mappings under it are covered by its banner.
    /// </summary>
    private static List<string> Problems(OverallStatus status) => status.Condition switch
    {
        OverallCondition.AllLinked => [],
        OverallCondition.NeedsAttention => status.Attention.Select(m => $"{m.MappingId}:{m.Word.Word}").ToList(),
        _ => [$"condition:{status.Condition}"],
    };
}
