namespace SaveTether.Gui.Presentation;

/// <summary>
/// The footer's background status, paced so each line can be read (<see cref="Pacing.StatusMinimum"/>). A scan runs
/// at its own speed and may start and end between two draws, so every line is offered the moment it changes:
/// a count replaces a count still waiting to be shown, but "Idle" and a running line never replace each other,
/// so a scan that starts and ends inside the hold is still shown running before the footer says Idle.
/// </summary>
public sealed class StatusPacer
{
    private readonly string _idle;
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly List<string> _waiting = [];
    private string _last;
    private string _shownLine;
    private long? _shownAt;

    public StatusPacer(string idle, TimeProvider? time = null)
    {
        _idle = idle;
        _last = idle;
        _shownLine = idle;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The line on show.</summary>
    public string Shown
    {
        get
        {
            lock (_gate)
            {
                return _shownLine;
            }
        }
    }

    /// <summary>From any thread, whenever the status changes.</summary>
    public void Offer(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        lock (_gate)
        {
            if (line == _last)
            {
                return;
            }

            _last = line;
            if (_waiting.Count > 0 && IsIdle(_waiting[^1]) == IsIdle(line))
            {
                _waiting[^1] = line;
            }
            else
            {
                _waiting.Add(line);
            }
        }
    }

    /// <summary>
    /// The next line to draw, when the one on show has been up long enough; otherwise null, and
    /// <paramref name="wait"/> is how long until it may change (zero when nothing is waiting).
    /// </summary>
    public string? TryNext(out TimeSpan wait)
    {
        lock (_gate)
        {
            wait = TimeSpan.Zero;
            if (_waiting.Count == 0)
            {
                return null;
            }

            if (_shownAt is { } at)
            {
                var left = Pacing.StatusWait(_time.GetElapsedTime(at));
                if (left > TimeSpan.Zero)
                {
                    wait = left;
                    return null;
                }
            }

            var next = _waiting[0];
            _waiting.RemoveAt(0);
            _shownLine = next;
            _shownAt = _time.GetTimestamp();
            wait = _waiting.Count > 0 ? Pacing.StatusMinimum : TimeSpan.Zero;
            return next;
        }
    }

    private bool IsIdle(string line) => line == _idle;
}
