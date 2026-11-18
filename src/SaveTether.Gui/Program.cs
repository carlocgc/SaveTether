using System.Net.Sockets;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using SaveTether.Core.Storage;
using SaveTether.Gui.Presentation;

namespace SaveTether.Gui;

/// <summary>How the app was started.</summary>
/// <param name="Minimized">Started with <c>--minimized</c>, as the autostart entry does: the tray icon only.</param>
internal sealed record StartOptions(string LocalFolder, bool Minimized, GuiLog Log);

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var folder = LocalStore.DefaultFolder();
        var log = new GuiLog(folder);
        App.Options = new StartOptions(folder, args.Contains("--minimized", StringComparer.Ordinal), log);

        SingleInstance? instance = null;
        try
        {
            instance = SingleInstance.TryClaim(folder, () => Dispatcher.UIThread.Post(() => App.Instance?.ShowWindow()));
            if (instance is null)
            {
                // Another copy is running: it shows its window, and this one is done.
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

        using (instance)
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
