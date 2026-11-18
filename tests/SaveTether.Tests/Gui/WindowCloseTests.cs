using SaveTether.Gui.Presentation;

namespace SaveTether.Tests.Gui;

/// <summary>Section 2: what closing the window does, and that the end of the session is never vetoed.</summary>
public class WindowCloseTests
{
    [Fact]
    public void The_owner_closing_hides_to_the_tray_or_quits_without_one()
    {
        Assert.Equal(CloseAction.HideToTray, WindowClose.Decide(CloseCause.Owner, trayPresent: true));
        Assert.Equal(CloseAction.Quit, WindowClose.Decide(CloseCause.Owner, trayPresent: false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_restart_shutdown_or_quit_lets_the_window_close(bool trayPresent)
    {
        // Found on the owner's PC: the close was cancelled to hide to the tray, and Windows reported SaveTether as
        // preventing the restart.
        Assert.Equal(CloseAction.LetClose, WindowClose.Decide(CloseCause.SessionEnding, trayPresent));
        Assert.Equal(CloseAction.LetClose, WindowClose.Decide(CloseCause.AppShutdown, trayPresent));
    }
}
