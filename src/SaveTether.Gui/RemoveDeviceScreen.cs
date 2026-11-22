using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SaveTether.Core.Operations;
using SaveTether.Core.Storage;
using SaveTether.Gui.Presentation;

namespace SaveTether.Gui;

/// <summary>
/// Remove this device (desktop-app-spec.md, "Maintenance"): a screen of two pages, like Add app's plan. Page 1 takes the choices
/// (<see cref="RemoveChoicesState"/>), page 2 lists what the removal will do. Remove then runs <see cref="ActionFlow.RemoveThisDeviceAsync"/>:
/// the typed phrases, the removal and the result. When it is done a check finds the device not set up and the window shows setup.
/// </summary>
internal sealed class RemoveDeviceScreen : UserControl, IScreen
{
    /// <summary>The way back, shown by the window above the scrolling page.</summary>
    public ContentControl Bar { get; } = new();

    private readonly LocalStore _local;
    private readonly Func<Func<ActionFlow, Task>, Task> _operate;
    private readonly ContentControl _page = new();
    private readonly RemoveChoicesState _state = new();

    public RemoveDeviceScreen(string localFolder, Func<Func<ActionFlow, Task>, Task> operate, Action close)
    {
        _local = new LocalStore(localFolder);
        _operate = operate;
        Bar.Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = RemoveDeviceFlow.Title, FontSize = 22, FontWeight = FontWeight.SemiBold },
                Navigation.BackRow("Back", close),
            },
        };
        Content = _page;
        _ = ShowChoicesAsync();
    }

    private async Task ShowChoicesAsync()
    {
        RemoveDeviceFlow.Start start;
        try
        {
            start = await Task.Run(() => RemoveDeviceFlow.Load(_local));
        }
        catch (Exception e) when (e is StoreException or IOException or UnauthorizedAccessException)
        {
            App.Options.Log.Write($"remove device: {e.GetType().Name}: {e.Message}");
            _page.Content = new TextBlock { Text = e.Message, TextWrapping = TextWrapping.Wrap };
            return;
        }

        var page = new StackPanel { Spacing = 10 };
        page.Children.Add(new TextBlock { Text = $"Remove '{start.DeviceName}' from the sync folder. {RemoveDeviceFlow.Stays}", TextWrapping = TextWrapping.Wrap });

        var combos = new List<ComboBox>();
        if (start.Orphans.Count > 0)
        {
            page.Children.Add(Navigation.GroupHeader("Mappings only this device uses"));
            var choices = new[] { OrphanChoice.Keep, OrphanChoice.DeleteMapping, OrphanChoice.DeleteWithSaves };
            foreach (var id in start.Orphans)
            {
                var combo = new ComboBox
                {
                    ItemsSource = choices.Select(RemoveDeviceFlow.OrphanLabel).ToList(),
                    SelectedIndex = Array.IndexOf(choices, _state.Orphans.GetValueOrDefault(id, OrphanChoice.Keep)),
                    MinWidth = 240,
                };
                AutomationProperties.SetName(combo, $"What to do with {id}");
                combo.SelectionChanged += (_, _) => _state.Orphans[id] = choices[combo.SelectedIndex];
                combos.Add(combo);
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                row.Children.Add(new TextBlock { Text = id, VerticalAlignment = VerticalAlignment.Center });
                Grid.SetColumn(combo, 1);
                row.Children.Add(combo);
                page.Children.Add(row);
            }
        }

        page.Children.Add(Navigation.GroupHeader("Backups"));
        var backups = new CheckBox { Content = RemoveDeviceFlow.BackupsLabel(delete: true), IsChecked = _state.DeleteBackups };
        backups.IsCheckedChanged += (_, _) => _state.DeleteBackups = backups.IsChecked == true;
        page.Children.Add(backups);

        if (start.DeleteEverything.Offered)
        {
            page.Children.Add(Navigation.GroupHeader("Last device"));
            var everything = new CheckBox { Content = RemoveDeviceFlow.DeleteEverythingLabel, IsChecked = _state.DeleteEverything };
            everything.IsCheckedChanged += (_, _) =>
            {
                _state.DeleteEverything = everything.IsChecked == true;
                foreach (var combo in combos)
                {
                    combo.IsEnabled = !_state.DeleteEverything;
                }
            };
            page.Children.Add(everything);
        }
        else if (start.DeleteEverything.Reason is { } reason)
        {
            page.Children.Add(new TextBlock { Text = $"{RemoveDeviceFlow.DeleteEverythingLabel}: not offered. {reason}", TextWrapping = TextWrapping.Wrap, Opacity = 0.75, FontSize = 13 });
        }

        var next = new Button { Content = "Next", Margin = new Thickness(0, 8, 0, 0) };
        next.Click += (_, _) => _ = ShowPlanAsync();
        page.Children.Add(next);
        _page.Content = page;
    }

    private async Task ShowPlanAsync()
    {
        var choices = _state.ToChoices();
        RemoveDeviceFlow.PlanView view;
        try
        {
            view = await Task.Run(() => RemoveDeviceFlow.ViewPlan(_local, choices));
        }
        catch (Exception e) when (e is StoreException or IOException or UnauthorizedAccessException)
        {
            App.Options.Log.Write($"remove device plan: {e.GetType().Name}: {e.Message}");
            view = new RemoveDeviceFlow.PlanView([e.Message], true);
        }

        var page = new StackPanel { Spacing = 6 };
        page.Children.Add(new TextBlock { Text = view.Refused ? "Nothing can be removed yet:" : "This is what will happen:", FontWeight = FontWeight.SemiBold });
        foreach (var line in view.Lines)
        {
            page.Children.Add(new SelectableTextBlock { Text = line, TextWrapping = TextWrapping.Wrap });
        }

        var back = new Button { Content = "Back to choices" };
        back.Click += (_, _) => _ = ShowChoicesAsync();
        var remove = new Button { Content = "Remove", IsEnabled = !view.Refused };
        Danger.Style(remove);
        remove.Click += (_, _) => _ = _operate(flow => flow.RemoveThisDeviceAsync(choices));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0), Children = { back, remove } };
        page.Children.Add(buttons);
        _page.Content = page;
    }
}
