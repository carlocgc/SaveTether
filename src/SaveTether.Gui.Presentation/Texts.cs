namespace SaveTether.Gui.Presentation;

/// <summary>The app's own sentences (desktop-app-spec.md, principle 3). The core's message is shown under them.</summary>
public static class Texts
{
    public const string AppName = "SaveTether";

    /// <summary>The first time the window is closed while the tray icon stays (section 2).</summary>
    public const string StillRunningInTray = "SaveTether is still running in the tray. Quit it from the tray icon's menu.";

    public const string QuitRefused = "Wait for the current operation to finish.";

    public const string OpenWindow = "Open SaveTether";

    public const string CheckNow = "Check now";

    public const string Quit = "Quit";

    /// <summary>The banner's second line: the core's message, except for B3, whose one sentence holds its reason.</summary>
    public static string? BannerDetail(OverallStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.Condition == OverallCondition.SyncRootUnavailable ? null : status.Detail;
    }

    /// <summary>The banner sentence for a device-wide condition (section 4.1), or null when there is none.</summary>
    public static string? Banner(OverallStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.Condition switch
        {
            OverallCondition.NotSetUp => "This device is not set up for SaveTether yet.",
            OverallCondition.UpdateNeeded =>
                "A newer version of SaveTether has written one of its files. Update SaveTether on this device before going on.",
            OverallCondition.CannotCheck => "SaveTether cannot check this device.",
            OverallCondition.Unfinished => "An operation did not finish. Nothing else can be done until it is recovered.",
            OverallCondition.SyncRootUnavailable =>
                $"The sync folder {status.Report?.SyncRoot} is not available: {status.Detail ?? "it does not exist"}. If it is on a drive or a network share, connect it.",
            OverallCondition.DeviceNameInUse =>
                $"Another SaveTether install uses the device name '{status.Report?.DeviceName}'. Two machines with one name would apply each other's folders.",
            OverallCondition.CheckFailed => "SaveTether's health checks keep failing. The details are in gui.log in its local folder.",
            _ => null,
        };
    }
}
