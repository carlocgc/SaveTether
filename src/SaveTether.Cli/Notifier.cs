using System.Diagnostics;
using System.Globalization;

namespace SaveTether.Cli;

/// <summary>Shows a desktop notification. Tests supply their own.</summary>
internal interface INotifier
{
    /// <summary>True when the notification was handed to the desktop.</summary>
    bool Notify(string title, string body);
}

/// <summary>
/// Desktop notifications without a new dependency: notify-send on Linux, a toast through
/// PowerShell on Windows. Both are best effort; a failure is written to the log instead.
/// </summary>
internal sealed class SystemNotifier : INotifier
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Builds a toast from two environment variables, so the text needs no escaping. It is shown
    /// under PowerShell's own app ID, which Windows already knows.
    /// </summary>
    private const string ToastScript =
        "$x=[Windows.UI.Notifications.ToastNotificationManager,Windows.UI.Notifications,ContentType=WindowsRuntime]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02);" +
        "$t=$x.GetElementsByTagName('text');" +
        "[void]$t.Item(0).AppendChild($x.CreateTextNode($env:SAVETETHER_TOAST_TITLE));" +
        "[void]$t.Item(1).AppendChild($x.CreateTextNode($env:SAVETETHER_TOAST_BODY));" +
        "[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\WindowsPowerShell\\v1.0\\powershell.exe').Show([Windows.UI.Notifications.ToastNotification]::new($x))";

    public bool Notify(string title, string body)
    {
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("powershell.exe") { ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", ToastScript } }
            : new ProcessStartInfo("notify-send") { ArgumentList = { "--app-name=SaveTether", title, body } };
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.Environment["SAVETETHER_TOAST_TITLE"] = title;
        start.Environment["SAVETETHER_TOAST_BODY"] = body;

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return false;
            }

            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(Timeout))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }
}

/// <summary>
/// The log the scheduled jobs write to, savetether.log in this device's local folder. It is kept
/// small: past 512 KiB, only the newer half is kept.
/// </summary>
internal static class RunLog
{
    public const string FileName = "savetether.log";

    private const long MaxBytes = 512 * 1024;

    public static string PathIn(string localFolder) => System.IO.Path.Combine(localFolder, FileName);

    public static void Append(string localFolder, DateTimeOffset when, IEnumerable<string> lines)
    {
        var path = PathIn(localFolder);
        Directory.CreateDirectory(localFolder);
        var stamp = when.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        File.AppendAllLines(path, lines.Select(line => $"{stamp} {line}"));

        var info = new FileInfo(path);
        if (info.Length > MaxBytes)
        {
            var kept = File.ReadAllLines(path);
            File.WriteAllLines(path, kept[(kept.Length / 2)..]);
        }
    }
}
