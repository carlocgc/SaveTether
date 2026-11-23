namespace SaveTether.Gui.Presentation;

/// <summary>
/// Numbers a page's loads so that only the latest may change the page. A load that finishes, or fails, after the
/// screen was left or another load began finds its number is no longer current and does nothing to the page; the
/// failure is still logged by the caller.
/// </summary>
public sealed class LatestLoad
{
    private int _current;

    /// <summary>Starts a load: every earlier one is stale from now on.</summary>
    public int Begin() => Interlocked.Increment(ref _current);

    /// <summary>Makes every load in flight stale (the screen was left, or another page opened).</summary>
    public void Invalidate() => Interlocked.Increment(ref _current);

    public bool IsCurrent(int load) => Volatile.Read(ref _current) == load;
}
