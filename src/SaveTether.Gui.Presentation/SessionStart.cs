using SaveTether.Core.Startup;

namespace SaveTether.Gui.Presentation;

/// <summary>
/// "Start with the session" (desktop-app-spec.md, section 11): the Settings switch, the entry rewritten with
/// the app's current path at each start while it is on, and banner B5 when the entry names a program that
/// is gone. Nothing here changes the entry unless the owner turned the switch on, or asks.
/// </summary>
/// <param name="store">The system's entry, or null where the desktop has none (the switch is then not offered).</param>
/// <param name="program">This app's own path, which the entry starts.</param>
public sealed class SessionStart(IAutostartStore? store, string program)
{
    public bool Available => store is not null;

    /// <summary>The switch's position: an entry is there, whatever program it names.</summary>
    public bool IsOn => Autostart.Check(store).Enabled;

    /// <summary>Banner B5's sentence, or null when the entry is off or its program is there.</summary>
    public string? MissingBanner => Autostart.Check(store).MissingProgram is { } missing
        ? $"SaveTether is set to start with the session, but the entry points to {missing}, which is missing."
        : null;

    /// <summary>The switch turned on or off, or B5's Fix it (on) and Turn it off (off). Answers why it failed, or null.</summary>
    public string? Set(bool on)
    {
        if (store is null)
        {
            return "This desktop has no place for an entry that starts programs with the session.";
        }

        try
        {
            if (on)
            {
                store.Write(program);
            }
            else
            {
                store.Remove();
            }

            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or Core.FileSystem.SafeFsException)
        {
            return $"{(on ? "The entry could not be written" : "The entry could not be removed")} at {store.Location}: {e.Message}";
        }
    }

    /// <summary>
    /// At each start: while the switch is on and the entry names another program, it is written again with this
    /// one (section 11, so a moved app keeps starting). Answers a line for the log when it did, or null.
    /// </summary>
    /// <param name="ownLocalFolder">
    /// This copy uses a local folder of its own (SAVETETHER_HOME): the entry is left alone, since it starts the program with the
    /// default one. A test copy took the owner's entry this way, and its uninstaller then deleted it (found on Windows).
    /// </param>
    public string? RefreshAtStart(bool ownLocalFolder = false)
    {
        var current = Autostart.Check(store).Program;
        if (current is null || string.Equals(current, program, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return null;
        }

        if (ownLocalFolder)
        {
            return $"autostart entry left at {current}: this copy uses its own local folder";
        }

        return Set(on: true) is { } problem
            ? $"autostart entry not refreshed: {problem}"
            : $"autostart entry refreshed: {current} -> {program}";
    }
}
