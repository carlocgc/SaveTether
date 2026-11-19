using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SaveTether.Core.Execution;
using SaveTether.Core.Storage;
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
    private readonly Func<Func<ActionFlow, Task>, Task> _operate;
    private readonly ActionFlow _actions;
    private SetupView? _setup;
    private Control? _screen;
    private bool _notHereOpen;

    /// <param name="operate">Runs one operation: the app marks itself busy around it and checks after it.</param>
    public MainWindow(string localFolder, Action checkNow, Action quit, Func<Func<ActionFlow, Task>, Task> operate)
    {
        _localFolder = localFolder;
        _checkNow = checkNow;
        _operate = operate;
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
        _actions = new ActionFlow(localFolder, _overlay, App.Options.Log);

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

    /// <summary>Tells something in the window, as a dialog with OK.</summary>
    public Task InformAsync(string title, string text) => _overlay.InformAsync(title, text);

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
            _screen = null;
            _setup ??= new SetupView(_localFolder, _overlay, _checkNow);
            _body.Content = _setup;
            return;
        }

        // Add app stays open through checks in the background, like setup: it is rebuilt only by the owner.
        _setup = null;
        _body.Content = _screen ?? _rows;
        _banners.Children.Clear();
        if (status.Condition == OverallCondition.Unfinished)
        {
            // Banner B2 names what did not finish, from the journal, and offers Recover (section 9.5).
            var recover = new Button { Content = "Recover" };
            recover.Click += (_, _) => _ = _operate(flow => flow.RecoverAsync());
            _banners.Children.Add(Banner(ActionTexts.Unfinished(Journal.For(new LocalStore(_localFolder)).TryRead()), null, recover));
        }
        else if (Texts.Banner(status) is { } banner)
        {
            _banners.Children.Add(Banner(banner, status.Detail));
        }

        // Banner B5: the entry that starts the app with the session names a program that is gone (section 11).
        // Read each check; nothing is changed unless the owner presses one of its buttons.
        if (App.Session.MissingBanner is { } missing)
        {
            var fix = new Button { Content = "Fix it" };
            fix.Click += (_, _) => SetSessionStart(on: true);
            var off = new Button { Content = "Turn it off" };
            off.Click += (_, _) => SetSessionStart(on: false);
            _banners.Children.Add(Banner(missing, null, fix, off));
        }

        if (result.LockHeldBy is { } holder)
        {
            var started = holder.StartedUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
            _banners.Children.Add(Banner($"Another SaveTether operation is running ({holder.Command}, started {started}). Actions are paused until it finishes.", null));
        }

        // While B1, B2 or B3 is shown, or another process holds the lock, the core would refuse every action (section 4.1).
        var actionsAllowed = result.LockHeldBy is null
            && status.Condition is not (OverallCondition.UpdateNeeded or OverallCondition.Unfinished or OverallCondition.SyncRootUnavailable);
        _rows.Children.Clear();
        var page = AppsPage.From(status);

        // The page's actions (section 5): Add app, and Apply all when at least one row is ready to link
        // (found on the Deck hidden with one left).
        var pageActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 8) };
        var addApp = new Button { Content = "Add app", IsEnabled = actionsAllowed };
        addApp.Click += (_, _) => OpenScreen(new AddAppScreen(_localFolder, _overlay, _operate, CloseScreen, AddAppStart.Catalog));
        pageActions.Children.Add(addApp);
        if (page.ForApplyAll.Count > 0)
        {
            var applyAll = new Button { Content = $"Apply all ({page.ForApplyAll.Count})", IsEnabled = actionsAllowed };
            applyAll.Click += (_, _) => _ = _operate(flow => flow.ApplyAllAsync(page.ForApplyAll));
            pageActions.Children.Add(applyAll);
        }

        // Backups only reads until Restore, which the core refuses itself while the device is not fit for it.
        var backups = new Button { Content = "Backups" };
        backups.Click += (_, _) => OpenScreen(new BackupsScreen(_localFolder, _operate, CloseScreen, null));
        pageActions.Children.Add(backups);
        var settings = new Button { Content = "Settings" };
        settings.Click += (_, _) => OpenScreen(new SettingsScreen(_localFolder, App.Session, _overlay, CloseScreen));
        pageActions.Children.Add(settings);
        _rows.Children.Add(pageActions);

        foreach (var group in page.Groups)
        {
            _rows.Children.Add(new TextBlock { Text = group.AppName, FontSize = 17, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 10, 0, 2) });
            foreach (var row in group.Rows)
            {
                _rows.Children.Add(Row(row, actionsAllowed));
            }
        }

        // Set up on other devices and not wanted here is normal, so these are listed last and collapsed (section 5.1).
        if (page.NotOnThisDevice.Count > 0)
        {
            var list = new StackPanel { Spacing = 2 };
            foreach (var row in page.NotOnThisDevice)
            {
                // No app heading above these, so each names its app.
                list.Children.Add(Row(row, actionsAllowed, withApp: true));
            }

            // Rebuilt at every check, so whether it is open is kept here: a check must not close it under the owner.
            var expander = new Expander
            {
                Header = $"Not on this device ({page.NotOnThisDevice.Count})",
                Content = list,
                IsExpanded = _notHereOpen,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 12, 0, 0),
            };
            expander.Expanded += (_, _) => _notHereOpen = true;
            expander.Collapsed += (_, _) => _notHereOpen = false;
            _rows.Children.Add(expander);
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

    private static Border Banner(string sentence, string? detail, params Button[] actions)
    {
        var text = new StackPanel { Spacing = 4, Children = { new TextBlock { Text = sentence, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold } } };
        if (detail is not null)
        {
            text.Children.Add(new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, Opacity = 0.8, FontSize = 13 });
        }

        if (actions.Length > 0)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
            foreach (var action in actions)
            {
                row.Children.Add(action);
            }

            text.Children.Add(row);
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

    /// <summary>
    /// One row: state, word, the spec's sentence and the folder, the main action as a button, and the others
    /// under "More" as a list opened by click or keyboard, never by hover (section 12).
    /// </summary>
    private Grid Row(AppRow row, bool actionsAllowed, bool withApp = false)
    {
        var mapping = row.Mapping;
        var name = withApp ? $"{mapping.AppName} {mapping.Role}" : mapping.Role;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,150,170,*,Auto,Auto"), Margin = new Thickness(0, 4) };
        Add(grid, 0, new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(Icons.ColourOf(mapping.Word.Shade)), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        Add(grid, 1, new TextBlock { Text = name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        Add(grid, 2, new TextBlock { Text = mapping.Word.Word, VerticalAlignment = VerticalAlignment.Center });
        Add(grid, 3, new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = row.Sentence, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = mapping.Health.Source ?? string.Empty, Opacity = 0.65, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis },
            },
        });

        var role = $"{mapping.AppName} {mapping.Role}";
        if (row.Main is { } main)
        {
            var button = new Button { Content = AppsPage.Label(main), IsEnabled = Allowed(main, actionsAllowed), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(button, $"{AppsPage.Label(main)}: {role}");
            button.Click += (_, _) => Do(main, row);
            Add(grid, 4, button);
        }

        if (row.Others.Count > 0)
        {
            var menu = new MenuFlyout();
            foreach (var other in row.Others)
            {
                var item = new MenuItem { Header = AppsPage.Label(other), IsEnabled = Allowed(other, actionsAllowed) };
                item.Click += (_, _) => Do(other, row);
                menu.Items.Add(item);
            }

            var more = new Button { Content = "More", Flyout = menu, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(more, $"More actions: {role}");
            Add(grid, 5, more);
        }

        return grid;
    }

    /// <summary>Looking needs nothing; an operation needs the device in a state the core accepts.</summary>
    private static bool Allowed(RowAction action, bool actionsAllowed) =>
        actionsAllowed || action is not (RowAction.Apply or RowAction.Revert or RowAction.UseThisCopy or RowAction.StopUsingHere
            or RowAction.FindAppAgain or RowAction.ChooseFolderAgain or RowAction.AddHere);

    /// <summary>Banner B5's Fix it (write the entry with this app's path) or Turn it off (remove it); then a check redraws.</summary>
    private void SetSessionStart(bool on)
    {
        if (App.Session.Set(on) is { } problem)
        {
            App.Options.Log.Write(problem);
            _ = _overlay.InformAsync("Nothing was changed", problem);
            return;
        }

        App.Options.Log.Write($"start with the session: {(on ? "entry rewritten" : "off")}, from banner B5");
        _checkNow();
    }

    /// <summary>A page in place of the rows: Add app (section 8) or Backups (section 9.7). Checks in the background keep it.</summary>
    private void OpenScreen(Control screen)
    {
        _screen = screen;
        _body.Content = screen;
    }

    private void CloseScreen()
    {
        _screen = null;
        _body.Content = _rows;
        _checkNow();
    }

    private void Do(RowAction action, AppRow row)
    {
        var mapping = row.Mapping;
        var log = App.Options.Log;
        switch (action)
        {
            case RowAction.Apply:
                _ = _operate(flow => flow.ApplyAsync(mapping, useThisCopy: false));
                break;
            case RowAction.UseThisCopy:
                _ = _operate(flow => flow.ApplyAsync(mapping, useThisCopy: true));
                break;
            case RowAction.Revert:
                _ = _operate(flow => flow.RevertAsync(mapping));
                break;
            case RowAction.StopUsingHere:
                _ = _operate(flow => flow.StopUsingAsync(mapping));
                break;
            case RowAction.CheckAgain:
                _checkNow();
                break;
            case RowAction.FindAppAgain or RowAction.AddHere when mapping.HasDefinition:
                OpenScreen(new AddAppScreen(_localFolder, _overlay, _operate, CloseScreen, AddAppStart.App(mapping.AppId)));
                break;
            case RowAction.ChooseFolderAgain or RowAction.AddHere:
                // A folder added by hand: the hand form for this mapping (section 8).
                OpenScreen(new AddAppScreen(_localFolder, _overlay, _operate, CloseScreen, AddAppStart.Folder(mapping.MappingId)));
                break;
            case RowAction.Backups:
                OpenScreen(new BackupsScreen(_localFolder, _operate, CloseScreen, mapping.AppId));
                break;
            case RowAction.OpenFolder when mapping.Health.Source is { } source:
                FolderOpener.Open(source, log);
                break;
            case RowAction.OpenFolderParent when System.IO.Path.GetDirectoryName(mapping.Health.Source) is { } parent:
                FolderOpener.Open(parent, log);
                break;
            case RowAction.OpenSharedFolder:
                FolderOpener.Open(mapping.Health.Target, log);
                break;
            case RowAction.OpenSharedFolderParent when System.IO.Path.GetDirectoryName(mapping.Health.Target) is { } sharedParent:
                FolderOpener.Open(sharedParent, log);
                break;
            case RowAction.Details:
                _ = _overlay.InformAsync($"{mapping.AppName} {mapping.Role}: {mapping.Word.Word}", mapping.Health.Detail.Text);
                break;
        }
    }

    /// <summary>The flow the window's operations run through.</summary>
    public ActionFlow Actions => _actions;

    private static void Add(Grid grid, int column, Control control)
    {
        Grid.SetColumn(control, column);
        grid.Children.Add(control);
    }
}
