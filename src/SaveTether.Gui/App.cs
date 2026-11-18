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

    public static StartOptions Options { get; set; } = null!;

    public static App? Instance { get; private set; }

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
            _monitor = HealthMonitor.For(new LocalStore(Options.LocalFolder));
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

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Shows the window and brings it to the front, creating it the first time; then checks.</summary>
    public void ShowWindow()
    {
        if (_window is null)
        {
            _window = new MainWindow(Options.LocalFolder, CheckNow, Quit);
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

    /// <summary>Runs a check off the UI thread. One already running is not started twice.</summary>
    public void CheckNow()
    {
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
        // Operations come with the next part of phase 3; until then nothing can be running here.
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

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeQuits)
        {
            Quit();
            return;
        }

        // Closing hides the window; the tray icon keeps running.
        e.Cancel = true;
        _window!.Hide();
        if (!_toldAboutTray)
        {
            _toldAboutTray = true;
            Notifier.Show(Texts.StillRunningInTray, Log);
        }
    }
}
