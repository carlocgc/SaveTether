using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SaveTether.Gui.Presentation;

namespace SaveTether.Gui;

/// <summary>
/// The main window as far as the shell goes (desktop-app-spec.md, section 4): the header with the overall
/// state and "Check now", a banner for a device-wide condition, and each mapping's state. The pages and
/// their actions come with the next parts of phase 3. Built in code: no XAML, nothing bound by reflection.
/// </summary>
internal sealed class MainWindow : Window
{
    private readonly TextBlock _device = new() { FontSize = 20, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _syncRoot = new() { Opacity = 0.75 };
    private readonly Ellipse _dot = new() { Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _headline = new() { FontSize = 17, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _checkedAt = new() { Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _banners = new() { Spacing = 8 };
    private readonly StackPanel _rows = new() { Spacing = 2 };
    private readonly ScrollViewer _body;
    private readonly Overlay _overlay;
    private readonly string _localFolder;
    private readonly Action _checkNow;
    private SetupView? _setup;

    public MainWindow(string localFolder, Action checkNow, Action quit)
    {
        _localFolder = localFolder;
        _checkNow = checkNow;
        Title = Texts.AppName;
        Icon = Icons.For(amber: false);
        MinWidth = 800;
        MinHeight = 560;
        Width = 1000;
        Height = 680;
        FontSize = 15;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var check = new Button { Content = Texts.CheckNow, VerticalAlignment = VerticalAlignment.Center };
        check.Click += (_, _) => checkNow();
        AutomationProperties.SetName(_headline, "Overall state");

        var state = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _dot, _headline, check, _checkedAt } };
        var header = new StackPanel { Spacing = 4, Children = { _device, _syncRoot, state } };
        var footer = new TextBlock { Text = $"Local folder: {localFolder}", Opacity = 0.6, FontSize = 13 };

        _body = new ScrollViewer { Content = _rows, Margin = new Thickness(0, 12, 0, 12) };
        _overlay = new Overlay(new DockPanel
        {
            Margin = new Thickness(24),
            Children =
            {
                Dock(header, Avalonia.Controls.Dock.Top),
                Dock(new Border { Height = 16 }, Avalonia.Controls.Dock.Top),
                Dock(_banners, Avalonia.Controls.Dock.Top),
                Dock(footer, Avalonia.Controls.Dock.Bottom),
                _body,
            },
        });
        Content = _overlay;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Q && e.KeyModifiers == KeyModifiers.Control)
            {
                quit();
                e.Handled = true;
            }
            else if (e.Key == Key.F5)
            {
                checkNow();
                e.Handled = true;
            }
        };

        _headline.Text = "Checking";
    }

    /// <summary>Section 10's "anything else": the app keeps running, and the details are in gui.log.</summary>
    public Task ShowUnexpectedErrorAsync(string message) =>
        _overlay.InformAsync(
            "Something went wrong",
            $"Something went wrong: {message}\n\nSaveTether is still running. The details are in gui.log in {_localFolder}.");

    /// <summary>Shows one check's result. <paramref name="iconState"/> is what the tray shows, which may lag while another process works.</summary>
    public void Show(CheckResult result, OverallStatus? iconState)
    {
        var status = result.Shown;
        _device.Text = status.Report is { } report ? $"This device: {report.DeviceName}" : Texts.AppName;
        _syncRoot.Text = status.Report is { } r ? $"Sync folder: {r.SyncRoot}" : string.Empty;
        _dot.Fill = new SolidColorBrush(status.IsAmber ? Icons.Amber : Icons.Green);
        _headline.Text = status.Headline;
        _checkedAt.Text = "Checked " + DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
        Icon = Icons.For((iconState ?? status).IsAmber);

        // Not set up: the setup pages take the place of the rows (section 6). Made once, so a check in the
        // background does not lose what the owner has typed.
        if (status.Condition == OverallCondition.NotSetUp)
        {
            _banners.Children.Clear();
            _setup ??= new SetupView(_localFolder, _overlay, _checkNow);
            _body.Content = _setup;
            return;
        }

        _setup = null;
        _body.Content = _rows;
        _banners.Children.Clear();
        if (Texts.Banner(status) is { } banner)
        {
            _banners.Children.Add(Banner(banner, status.Detail));
        }

        if (result.LockHeldBy is { } holder)
        {
            var started = holder.StartedUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
            _banners.Children.Add(Banner($"Another SaveTether operation is running ({holder.Command}, started {started}). Actions are paused until it finishes.", null));
        }

        _rows.Children.Clear();
        foreach (var mapping in status.Mappings)
        {
            _rows.Children.Add(Row(mapping));
        }

        if (status.Mappings.Count == 0 && status.Report is not null)
        {
            _rows.Children.Add(new TextBlock { Text = "No apps are set up yet.", Opacity = 0.75 });
        }
    }

    private static Control Dock(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    private static Border Banner(string sentence, string? detail)
    {
        var text = new StackPanel { Spacing = 4, Children = { new TextBlock { Text = sentence, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold } } };
        if (detail is not null)
        {
            text.Children.Add(new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, Opacity = 0.8, FontSize = 13 });
        }

        return new Border
        {
            Child = text,
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Icons.Amber),
            Background = new SolidColorBrush(Icons.Amber, 0.12),
        };
    }

    private static Grid Row(MappingState mapping)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,240,200,*"), Margin = new Thickness(0, 6) };
        Add(grid, 0, new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(Icons.ColourOf(mapping.Word.Shade)), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        Add(grid, 1, new TextBlock { Text = $"{mapping.AppName} {mapping.Role}", FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        Add(grid, 2, new TextBlock { Text = mapping.Word.Word });
        Add(grid, 3, new TextBlock { Text = mapping.Health.Source ?? string.Empty, Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis });
        return grid;
    }

    private static void Add(Grid grid, int column, Control control)
    {
        Grid.SetColumn(control, column);
        grid.Children.Add(control);
    }
}
