using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SaveTether.Core.Storage;
using SaveTether.Gui.Presentation;

namespace SaveTether.Gui;

/// <summary>
/// The Maintenance screen (desktop-app-spec.md, "Maintenance"), in place of the Apps page while open: the overview, this device with
/// Rename and Remove, the other devices, and the orphaned mappings. The rows and texts are <see cref="MaintenancePage"/>; each action
/// runs through <see cref="ActionFlow"/> as an operation, after which the screen reads again.
/// </summary>
internal sealed class MaintenanceScreen : UserControl, IScreen
{
    /// <summary>The way back, shown by the window above the scrolling page.</summary>
    public ContentControl Bar { get; } = new();

    private readonly LocalStore _local;
    private readonly Func<Func<ActionFlow, Task>, Task> _operate;
    private readonly Action _openBackups;
    private readonly Action _openRemove;
    private readonly ContentControl _page = new();

    public MaintenanceScreen(string localFolder, Func<Func<ActionFlow, Task>, Task> operate, Action openBackups, Action openRemove, Action close)
    {
        _local = new LocalStore(localFolder);
        _operate = operate;
        _openBackups = openBackups;
        _openRemove = openRemove;
        Bar.Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Maintenance", FontSize = 22, FontWeight = FontWeight.SemiBold },
                Navigation.BackRow("Back", close),
            },
        };
        Content = _page;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var loaded = await Task.Run(() => MaintenancePage.Load(_local));
            Show(loaded);
        }
        catch (Exception e) when (e is StoreException or IOException or UnauthorizedAccessException)
        {
            App.Options.Log.Write($"maintenance: {e.GetType().Name}: {e.Message}");
            _page.Content = new TextBlock { Text = e.Message, TextWrapping = TextWrapping.Wrap };
        }
    }

    private void Show(MaintenancePage.Loaded loaded)
    {
        var page = new StackPanel { Spacing = 8 };
        if (loaded.View is not { } view)
        {
            page.Children.Add(new TextBlock { Text = loaded.Blocker ?? MaintenancePage.BlockedBySyncRoot, TextWrapping = TextWrapping.Wrap });
            _page.Content = page;
            return;
        }

        var blocked = view.Blocker is not null;
        if (blocked)
        {
            page.Children.Add(new TextBlock { Text = view.Blocker, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold });
        }

        page.Children.Add(Navigation.GroupHeader("Overview"));
        var overview = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        foreach (var line in view.OverviewLines)
        {
            overview.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap });
        }

        // Level with the last overview line, the backups count it belongs to, not floating in the middle of the block.
        var openBackups = new Button { Content = "Open Backups", VerticalAlignment = VerticalAlignment.Bottom };
        openBackups.Click += (_, _) => _openBackups();
        page.Children.Add(Row(overview, openBackups));

        page.Children.Add(Navigation.GroupHeader("This device"));
        var taken = view.Others.Select(d => d.Name).ToList();
        if (view.This is { } self)
        {
            var text = new StackPanel
            {
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = $"Name: {self.Name}" },
                    new SelectableTextBlock { Text = $"Install ID: {self.InstallId}", Opacity = 0.7, FontSize = 13 },
                },
            };
            var rename = new Button { Content = "Rename", IsEnabled = !blocked, VerticalAlignment = VerticalAlignment.Center };
            rename.Click += (_, _) => _ = Do(flow => flow.RenameDeviceAsync(self.Name, taken));
            var remove = new Button { Content = "Remove this device", IsEnabled = !blocked, VerticalAlignment = VerticalAlignment.Center };
            Danger.Style(remove);
            remove.Click += (_, _) => _openRemove();
            page.Children.Add(Row(text, rename, remove));
        }
        else
        {
            page.Children.Add(new TextBlock { Text = "This device's file is not in the sync folder. A removal here did not finish.", TextWrapping = TextWrapping.Wrap });
        }

        page.Children.Add(Navigation.GroupHeader("Other devices"));
        if (view.Others.Count == 0)
        {
            page.Children.Add(new TextBlock { Text = "No other device uses this sync folder.", Opacity = 0.75 });
        }

        foreach (var device in view.Others)
        {
            var text = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = device.Name, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = $"Last active {device.Activity}, {device.Mappings}", Opacity = 0.75, FontSize = 13 },
                },
            };
            if (device.Note is { } note)
            {
                text.Children.Add(new TextBlock { Text = note, FontSize = 13, Foreground = new SolidColorBrush(Icons.Amber) });
            }

            var remove = new Button { Content = "Remove", IsEnabled = device.RemoveDisabled is null, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(remove, $"Remove device {device.Name}");
            remove.Click += (_, _) => _ = Do(flow => flow.RemoveOtherDeviceAsync(device.Name));
            page.Children.Add(Row(text, remove));
        }

        page.Children.Add(Navigation.GroupHeader("Orphaned mappings"));
        if (view.Orphans.Count == 0)
        {
            page.Children.Add(new TextBlock { Text = "None. Every mapping is used by a device.", Opacity = 0.75 });
        }

        foreach (var orphan in view.Orphans)
        {
            var delete = new Button { Content = "Delete mapping", IsEnabled = orphan.Disabled is null, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(delete, $"Delete mapping {orphan.MappingId}");
            delete.Click += (_, _) => _ = Do(flow => flow.DeleteOrphanAsync(orphan.MappingId, withSaves: false));
            var deleteSaves = new Button { Content = "Delete mapping and data", IsEnabled = orphan.Disabled is null, VerticalAlignment = VerticalAlignment.Center };
            Danger.Style(deleteSaves);
            AutomationProperties.SetName(deleteSaves, $"Delete mapping and data {orphan.MappingId}");
            deleteSaves.Click += (_, _) => _ = Do(flow => flow.DeleteOrphanAsync(orphan.MappingId, withSaves: true));
            page.Children.Add(Row(new TextBlock { Text = orphan.MappingId, VerticalAlignment = VerticalAlignment.Center }, delete, deleteSaves));
        }

        _page.Content = page;
    }

    private async Task Do(Func<ActionFlow, Task> action)
    {
        await _operate(action);
        await LoadAsync();
    }

    private static Grid Row(Control text, params Button[] buttons)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 3) };
        grid.Children.Add(text);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.AddRange(buttons);
        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);
        return grid;
    }
}
