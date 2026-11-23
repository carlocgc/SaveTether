using System.Diagnostics;
using System.Text;
using SaveTether.Gui.Presentation;

namespace SaveTether.Gui;

/// <summary>
/// Desktop notifications without a new dependency (desktop-app-spec.md, section 3.1): a toast through
/// Windows PowerShell on Windows, <c>notify-send</c> on Linux. Not shown while Settings' switch is off.
/// A notification that cannot be shown is logged and otherwise ignored; the tray icon still shows the state.
/// </summary>
internal static class Notifier
{
    /// <summary>
    /// SaveTether's app ID for toasts from a copy that is not installed, registered per user under
    /// <c>HKCU\Software\Classes\AppUserModelId</c> with its name; the installer removes it on uninstall.
    /// </summary>
    private const string AppId = "SaveTether";

    /// <summary>The app ID Windows knows PowerShell by, used when SaveTether's own cannot be registered.</summary>
    private const string PowerShellAppId = "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\WindowsPowerShell\\v1.0\\powershell.exe";

    private static string? _registeredAppId;

    // The text reaches the script through an environment variable, never inside the script, so nothing in it is run.
    private const string ToastScript = """
        $ErrorActionPreference = 'Stop'
        [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] > $null
        $xml = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02)
        $texts = $xml.GetElementsByTagName('text')
        $texts.Item(0).AppendChild($xml.CreateTextNode($env:SAVETETHER_TOAST_TITLE)) > $null
        $texts.Item(1).AppendChild($xml.CreateTextNode($env:SAVETETHER_TOAST_BODY)) > $null
        [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($env:SAVETETHER_TOAST_APP).Show([Windows.UI.Notifications.ToastNotification]::new($xml))
        """;

    public static void Show(string body, GuiLog log)
    {
        if (!new NotificationSetting(App.Options.LocalFolder).IsOn)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var start = OperatingSystem.IsWindows() ? Toast(body, _registeredAppId ??= Register(log)) : NotifySend(body);
                if (start is null)
                {
                    return;
                }

                using var process = Process.Start(start);
                if (process is not null && process.WaitForExit(15_000) && process.ExitCode != 0)
                {
                    log.Write($"notification not shown: {start.FileName} exited {process.ExitCode}");
                }
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                log.Write($"notification not shown: {e.Message}");
            }
        });
    }

    /// <summary>
    /// The app ID to send toasts as, worked out once per run. The installed copy sends as the installer's Start menu
    /// entry, whose app ID is the program's path: Windows then shows its name and icon. Any other copy registers
    /// <see cref="AppId"/> with SaveTether's name; Windows 11 ignored the registered icon (IconUri) when tried.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string Register(GuiLog log)
    {
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "SaveTether", "savetether-gui.exe");
        var entry = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "SaveTether.lnk");
        if (string.Equals(Environment.ProcessPath, installed, StringComparison.OrdinalIgnoreCase) && File.Exists(entry))
        {
            return installed;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + AppId);
            key.SetValue("DisplayName", Texts.AppName);
            return AppId;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            log.Write($"notifications sent as Windows PowerShell: SaveTether could not be registered: {e.Message}");
            return PowerShellAppId;
        }
    }

    private static ProcessStartInfo Toast(string body, string appId)
    {
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-EncodedCommand" })
        {
            start.ArgumentList.Add(arg);
        }

        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(ToastScript)));
        start.Environment["SAVETETHER_TOAST_TITLE"] = Texts.AppName;
        start.Environment["SAVETETHER_TOAST_BODY"] = body;
        start.Environment["SAVETETHER_TOAST_APP"] = appId;
        return start;
    }

    private static ProcessStartInfo? NotifySend(string body)
    {
        if (Tools.Find("notify-send") is not { } path)
        {
            return null;
        }

        var start = new ProcessStartInfo(path) { UseShellExecute = false };
        start.ArgumentList.Add("--app-name=" + Texts.AppName);
        start.ArgumentList.Add(Texts.AppName);
        start.ArgumentList.Add(body);
        return start;
    }
}

/// <summary>
/// Whether a tray host is running (desktop-app-spec.md, section 2, decision 1). On Linux, a tray icon is
/// shown only when something owns <c>org.kde.StatusNotifierWatcher</c> on the session bus; without one,
/// closing the window must quit, or the app would run with no way back in.
/// </summary>
internal static class TrayHost
{
    public static bool Present()
    {
        if (!OperatingSystem.IsLinux())
        {
            return true;
        }

        if (Tools.Find("dbus-send") is not { } dbusSend)
        {
            return false;
        }

        var start = new ProcessStartInfo(dbusSend) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[]
        {
            "--session", "--print-reply", "--dest=org.freedesktop.DBus", "/org/freedesktop/DBus",
            "org.freedesktop.DBus.NameHasOwner", "string:org.kde.StatusNotifierWatcher",
        })
        {
            start.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return false;
            }

            var output = process.StandardOutput.ReadToEnd();
            return process.WaitForExit(5_000) && process.ExitCode == 0 && output.Contains("boolean true", StringComparison.Ordinal);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }
}

/// <summary>"Open folder": the folder in the desktop's file manager. The path is passed as an argument, never through a shell.</summary>
internal static class FolderOpener
{
    public static void Open(string path, GuiLog log)
    {
        try
        {
            var start = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("explorer.exe") { UseShellExecute = false }
                : Tools.Find("xdg-open") is { } xdgOpen ? new ProcessStartInfo(xdgOpen) { UseShellExecute = false } : null;
            if (start is null)
            {
                log.Write("open folder: xdg-open is not on PATH");
                return;
            }

            start.ArgumentList.Add(path);
            using var process = Process.Start(start);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            log.Write($"open folder failed for '{path}': {e.Message}");
        }
    }
}

internal static class Tools
{
    /// <summary>The full path of a program on <c>PATH</c>, or null.</summary>
    public static string? Find(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => Path.Combine(folder, name))
            .FirstOrDefault(File.Exists);
}
