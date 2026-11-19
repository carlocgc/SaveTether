using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using SaveTether.Core.Storage;
using SaveTether.Gui.Presentation;

namespace SaveTether.Gui;

/// <summary>
/// The app shell (desktop-app-spec.md, sections 2, 3 and 3.1): the tray icon and its menu, the health
/// checks and their notifications, and the main window, which closing hides while a tray host is there.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001", Justification = "The application object lives as long as the process; the tray icon is disposed in Quit.")]
internal sealed class App : Application
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    private HealthMonitor _monitor = null!;
    private IClassicDesktopStyleApplicationLifetime _desktop = null!;
    private TrayIcon? _tray;
    private NativeMenuItem? _headline;
    private MainWindow? _window;
    private CheckResult? _last;
    private bool _closeQuits;
    private bool _toldAboutTray;
    private bool? _iconAmber;
    private bool _busy;
    private bool _started;

    public static StartOptions Options { get; set; } = null!;

    public static App? Instance { get; private set; }

    /// <summary>The autostart entry: the Settings switch and banner B5.</summary>
    public static SessionStart Session { get; private set; } = new(null, string.Empty);

    private static GuiLog Log => Options.Log;

    public override void Initialize()
    {
        Name = Texts.AppName;
        RequestedThemeVariant = ThemeVariant.Default;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Instance = this;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            Dispatcher.UIThread.UnhandledException += OnUnhandledException;

            // The install or update offer comes first (section 11, "Installing"); the app starts once it is answered.
            if (Options.Offer?.Offer() is { } offer)
            {
                _ = OfferAsync(Options.Offer, offer);
            }
            else
            {
                StartNormally();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Asks; on Not now remembers it and starts as usual (or, when another copy runs, shows that one). On yes: for an
    /// update while the installed copy runs, asks it to quit and waits for it; then installs, starts the installed copy
    /// and quits. A failure is said, and this copy carries on as usual where it can.
    /// </summary>
    private async Task OfferAsync(InstallOffer installer, InstallOfferView offer)
    {
        var window = new InstallWindow();
        var accepted = await window.AskAsync(offer);
        if (!accepted)
        {
            installer.Decline();
            Log.Write($"install offer declined for this location ({(offer.IsUpdate ? "update" : "install")})");
            window.Close();
            FinishWithoutInstalling();
            return;
        }

        if (Options.UpdateOnly)
        {
            window.Working("Closing the running SaveTether…");
            SingleInstance.RequestQuit(Options.LocalFolder);
            if (!await Program.TakeClaimAsync(Options.LocalFolder, TimeSpan.FromSeconds(20)))
            {
                Log.Write("update: the running copy did not quit");
                await window.InformAsync(
                    "SaveTether is still running",
                    "The running SaveTether did not close, perhaps because it is in the middle of something. Quit it from its tray menu, then start this copy again.");
                window.Close();
                _desktop.Shutdown();
                return;
            }

            Options = Options with { UpdateOnly = false };
        }

        window.Working(offer.IsUpdate ? "Updating SaveTether…" : "Installing SaveTether…");
        var (installed, problem) = await Task.Run(installer.Install);
        if (problem is not null)
        {
            Log.Write(problem);
            await window.InformAsync("Nothing was installed", problem);
        }

        if (!installed)
        {
            window.Close();
            StartNormally();
            return;
        }

        Log.Write($"installed {installer.Version} in {installer.Layout.AppFolder}; starting it");
        Program.ReleaseClaim();
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(installer.Layout.AppPath) { UseShellExecute = false });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
        {
            Log.Write($"the installed copy could not be started: {e.Message}");
            await window.InformAsync("Installed", $"SaveTether was installed in {installer.Layout.AppFolder}, but could not be started from there: {e.Message}");
        }

        window.Close();
        _desktop.Shutdown();
    }

    private void FinishWithoutInstalling()
    {
        if (Options.UpdateOnly)
        {
            // The installed copy is running: it shows its window, as for any second start.
            SingleInstance.SignalExisting(Options.LocalFolder);
            _desktop.Shutdown();
            return;
        }

        StartNormally();
    }

    private void StartNormally()
    {
        _started = true;
        _monitor = HealthMonitor.For(new LocalStore(Options.LocalFolder));

        // While starting with the session is on, the entry follows the app if it moved (section 11).
        Session = new SessionStart(Core.Startup.Autostart.ForSystem(), Environment.ProcessPath ?? string.Empty);
        if (Session.RefreshAtStart() is { } refreshed)
        {
            Log.Write(refreshed);
        }

        _closeQuits = !TrayHost.Present();
        if (!_closeQuits)
        {
            CreateTray();
        }
        else
        {
            Log.Write("no tray host found: closing the window quits");
        }

        if (!Options.Minimized || _closeQuits)
        {
            ShowWindow();
        }

        var timer = new DispatcherTimer(CheckInterval, DispatcherPriority.Background, (_, _) => CheckNow());
        timer.Start();
        CheckNow();
    }

    /// <summary>Shows the window and brings it to the front, creating it the first time; then checks.</summary>
    public void ShowWindow()
    {
        // While the install offer is still open the app has not started: its window brings the offer forward instead.
        if (!_started)
        {
            return;
        }

        if (_window is null)
        {
            _window = new MainWindow(Options.LocalFolder, CheckNow, Quit, OperateAsync);
            _window.Closing += OnWindowClosing;
            if (_last is not null)
            {
                _window.Show(_last, _monitor.Current);
            }
        }

        _window.Show();
        _window.WindowState = _window.WindowState == WindowState.Minimized ? WindowState.Normal : _window.WindowState;
        _window.Activate();
        CheckNow();
    }

    /// <summary>
    /// Runs one operation from the window. While it runs the app is busy: no check runs, since this process
    /// holds the lock and a folder may be renamed aside for a moment, and Quit is refused. A check follows.
    /// </summary>
    public async Task OperateAsync(Func<ActionFlow, Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (_busy || _window is null)
        {
            return;
        }

        _busy = true;
        try
        {
            await operation(_window.Actions);
        }
        finally
        {
            _busy = false;
            CheckNow();
        }
    }

    /// <summary>Runs a check off the UI thread. One already running is not started twice, and none runs during an operation.</summary>
    public void CheckNow()
    {
        if (_busy)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            var result = _monitor.Run();
            if (result is not null)
            {
                Dispatcher.UIThread.Post(() => Apply(result));
            }
        });
    }

    public void Quit()
    {
        // An operation is a journalled transaction: stopping one half way is what recover is for, not something to offer.
        if (_busy)
        {
            ShowWindow();
            _ = _window!.InformAsync("Not now", Texts.QuitRefused);
            return;
        }

        _tray?.Dispose();
        _desktop.Shutdown();
    }

    private void Apply(CheckResult result)
    {
        _last = result;
        if (result.Error is not null)
        {
            Log.Write($"health check failed: {result.Error.GetType().Name}: {result.Error.Message}");
        }

        if (_tray is not null)
        {
            if (_iconAmber != result.Icon.IsAmber)
            {
                _iconAmber = result.Icon.IsAmber;
                _tray.Icon = Icons.For(result.Icon.IsAmber);
            }

            _tray.ToolTipText = $"{Texts.AppName}: {result.Icon.Headline}";
            _headline!.Header = result.Icon.Headline;
        }

        _window?.Show(result, _monitor.Current);

        if (result.Notification is { } text)
        {
            Notifier.Show(text, Log);
        }
    }

    private void CreateTray()
    {
        _headline = new NativeMenuItem(Texts.CheckNow) { IsEnabled = false };
        var open = new NativeMenuItem(Texts.OpenWindow);
        open.Click += (_, _) => ShowWindow();
        var check = new NativeMenuItem(Texts.CheckNow);
        check.Click += (_, _) => CheckNow();
        var quit = new NativeMenuItem(Texts.Quit);
        quit.Click += (_, _) => Quit();

        var menu = new NativeMenu();
        menu.Items.Add(_headline);
        menu.Items.Add(open);
        menu.Items.Add(check);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(quit);
        menu.Opening += (_, _) => CheckNow();

        _tray = new TrayIcon { Icon = Icons.For(amber: false), ToolTipText = Texts.AppName, Menu = menu, IsVisible = true };
        _tray.Clicked += (_, _) => ShowWindow();
        _headline.Header = "Checking";
        TrayIcon.SetIcons(this, [_tray]);
    }

    /// <summary>
    /// An exception the app did not expect, from a click or a dialog (desktop-app-spec.md, section 10): logged
    /// with its stack, shown in words, and the app keeps running. A health check follows, since the journal,
    /// not the app, decides whether anything is unfinished.
    /// </summary>
    private void OnUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        var error = e.Exception is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : e.Exception;
        Log.Write($"unexpected error: {error}");
        if (_window is not null)
        {
            _ = _window.ShowUnexpectedErrorAsync(error.Message);
        }

        CheckNow();
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        var cause = e.CloseReason switch
        {
            WindowCloseReason.OSShutdown => CloseCause.SessionEnding,
            WindowCloseReason.ApplicationShutdown => CloseCause.AppShutdown,
            _ => CloseCause.Owner,
        };
        switch (WindowClose.Decide(cause, trayPresent: !_closeQuits))
        {
            case CloseAction.LetClose:
                // Never a veto of a log off, restart or shutdown (found on the owner's PC: Windows named SaveTether
                // as blocking a restart). The process ends with the session; an operation cut short is Recover's.
                if (cause == CloseCause.SessionEnding)
                {
                    Log.Write(_busy ? "session ending during an operation: Recover finishes it at the next start" : "session ending: quitting");
                    _tray?.Dispose();
                    _tray = null;
                    _desktop.Shutdown();
                }

                return;

            case CloseAction.Quit:
                Quit();
                return;

            default:
                // Closing hides the window; the tray icon keeps running.
                e.Cancel = true;
                _window!.Hide();
                if (!_toldAboutTray)
                {
                    _toldAboutTray = true;
                    Notifier.Show(Texts.StillRunningInTray, Log);
                }

                return;
        }
    }
}
