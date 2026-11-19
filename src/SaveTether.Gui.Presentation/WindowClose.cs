namespace SaveTether.Gui.Presentation;

/// <summary>What closing the main window does (desktop-app-spec.md, section 2).</summary>
public enum CloseAction
{
    /// <summary>The window hides; the tray icon keeps the app running.</summary>
    HideToTray,

    /// <summary>The app quits, unless an operation is running (then it says to wait).</summary>
    Quit,

    /// <summary>The session or the app is ending: let the window close, never cancel it.</summary>
    LetClose,
}

/// <summary>Why the window is closing, as far as the decision needs it.</summary>
public enum CloseCause
{
    /// <summary>The owner: the X button, Alt+F4, the taskbar's Close window.</summary>
    Owner,

    /// <summary>The operating system's session is ending: log off, restart, shut down.</summary>
    SessionEnding,

    /// <summary>The app itself is shutting down, after Quit.</summary>
    AppShutdown,
}

public static class WindowClose
{
    /// <summary>
    /// Closing by the owner hides to the tray when there is one, and quits otherwise. When the session or the
    /// app is ending, the close goes ahead: cancelling it then is a veto of the restart or shutdown, which
    /// Windows shows as SaveTether preventing it (found on the owner's PC). An operation running at that
    /// moment is the journal's to finish or undo, by Recover at the next start.
    /// </summary>
    public static CloseAction Decide(CloseCause cause, bool trayPresent) => cause switch
    {
        CloseCause.SessionEnding or CloseCause.AppShutdown => CloseAction.LetClose,
        _ => trayPresent ? CloseAction.HideToTray : CloseAction.Quit,
    };
}
