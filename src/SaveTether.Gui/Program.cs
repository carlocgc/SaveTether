using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using SaveTether.Core.Storage;
using SaveTether.Gui.Presentation;

namespace SaveTether.Gui;

/// <summary>How the app was started.</summary>
/// <param name="Minimized">Started with <c>--minimized</c>, as the autostart entry does: the tray icon only.</param>
/// <param name="Offer">The install or update offer for where this copy runs; null when there is none to make.</param>
/// <param name="UpdateOnly">Another copy holds the single-instance claim, and this one is here only to offer an update.</param>
internal sealed record StartOptions(string LocalFolder, bool Minimized, GuiLog Log, InstallOffer? Offer = null, bool UpdateOnly = false);

internal static class Program
{
    private static SingleInstance? _claim;

    /// <summary>This build's version, as "&lt;version&gt;+&lt;commit&gt;".</summary>
    public static string Version { get; } =
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    [STAThread]
    public static int Main(string[] args)
    {
        var folder = LocalStore.DefaultFolder();
        var log = new GuiLog(folder);
        var minimized = args.Contains("--minimized", StringComparer.Ordinal);

        // Not at login (--minimized): an offer is for when the owner started the app themselves.
        var offer = minimized ? null : FindOffer(folder, log);
        App.Options = new StartOptions(folder, minimized, log, offer);

        try
        {
            _claim = Claim(folder);
            if (_claim is null)
            {
                // Another copy is running. A newer copy offers to update it; otherwise the other shows its window.
                if (offer?.Offer() is { IsUpdate: true })
                {
                    App.Options = App.Options with { UpdateOnly = true };
                    return RunApp(args, log);
                }

                if (!SingleInstance.SignalExisting(folder))
                {
                    log.Write("another copy holds the single-instance lock but did not answer");
                }

                return 0;
            }
        }
        catch (Exception e) when (e is SocketException or IOException or UnauthorizedAccessException)
        {
            // Without the claim the app still works; only a second copy is not turned away.
            log.Write($"single-instance claim failed: {e.Message}");
        }

        try
        {
            return RunApp(args, log);
        }
        finally
        {
            ReleaseClaim();
        }
    }

    /// <summary>When the app asked itself to shut down (<see cref="Stopwatch.GetTimestamp"/>); 0 until it has.</summary>
    public static long ShutdownRequestedAt { get; set; }

    /// <summary>
    /// Runs the app, and logs when it returns: how long after the shutdown was requested, so a quit that hangs (found on the
    /// Deck, where KDE said SaveTether did not close at a restart) shows as a request with no exit after it. A signal that
    /// asks the process to end is logged too, and then handled as usual.
    /// </summary>
    private static int RunApp(string[] args, GuiLog log)
    {
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => log.Write($"signal received: {context.Signal}"));
        using var hangUp = PosixSignalRegistration.Create(PosixSignal.SIGHUP, context => log.Write($"signal received: {context.Signal}"));
        var code = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        log.Write(ShutdownRequestedAt == 0
            ? $"exited ({code}) without a shutdown request"
            : $"exited ({code}) {Stopwatch.GetElapsedTime(ShutdownRequestedAt).TotalMilliseconds:0} ms after shutdown was requested");
        return code;
    }

    /// <summary>Lets the installed copy, about to be started, become the one copy.</summary>
    public static void ReleaseClaim()
    {
        _claim?.Dispose();
        _claim = null;
    }

    /// <summary>
    /// For an update: waits for the copy that was asked to quit to let go of the claim, then holds it, so no copy starts
    /// while the files are replaced. False when it did not quit in time (it refuses during an operation).
    /// </summary>
    public static async Task<bool> TakeClaimAsync(string localFolder, TimeSpan wait)
    {
        var until = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < until)
        {
            try
            {
                if (Claim(localFolder) is { } claim)
                {
                    _claim = claim;
                    return true;
                }
            }
            catch (Exception e) when (e is SocketException or IOException or UnauthorizedAccessException)
            {
                // Try again until the time is up.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(true);
        }

        return false;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();

    private static SingleInstance? Claim(string folder) =>
        SingleInstance.TryClaim(
            folder,
            () => Dispatcher.UIThread.Post(() => App.Instance?.ShowWindow()),
            // A newer copy asks this one to quit, to update it: as Quit from the tray, so never during an operation.
            () => Dispatcher.UIThread.Post(() => App.Instance?.Quit()));

    private static InstallOffer? FindOffer(string folder, GuiLog log)
    {
        try
        {
            return InstallOffer.ForSystem(folder, AppContext.BaseDirectory, Version);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            log.Write($"install check failed: {e.Message}");
            return null;
        }
    }
}
