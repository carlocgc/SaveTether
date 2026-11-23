using SaveTether.Gui.Presentation;

namespace SaveTether.Tests.Gui;

/// <summary>Only the latest load of a page may change it, whether it ends well or not.</summary>
public class LatestLoadTests
{
    [Fact]
    public void A_newer_load_makes_the_older_one_stale_and_leaves_itself_current()
    {
        var loads = new LatestLoad();
        var older = loads.Begin();
        Assert.True(loads.IsCurrent(older));

        var newer = loads.Begin();

        Assert.False(loads.IsCurrent(older));
        Assert.True(loads.IsCurrent(newer));
    }

    [Fact]
    public void Leaving_the_screen_makes_every_load_stale()
    {
        var loads = new LatestLoad();
        var load = loads.Begin();

        loads.Invalidate();

        Assert.False(loads.IsCurrent(load));
    }

    [Fact]
    public void An_older_load_that_fails_after_a_newer_one_began_is_seen_as_stale_and_does_not_end_the_newer()
    {
        var loads = new LatestLoad();
        var older = loads.Begin();
        var newer = loads.Begin();

        // What the page's error handler asks before it draws anything: the older failure is only logged,
        // and nothing it does invalidates the newer load.
        var drewError = false;
        if (loads.IsCurrent(older))
        {
            drewError = true;
            loads.Invalidate();
        }

        Assert.False(drewError);
        Assert.True(loads.IsCurrent(newer));
    }
}
