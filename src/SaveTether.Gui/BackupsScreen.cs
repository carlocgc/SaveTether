using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SaveTether.Core.Storage;
using SaveTether.Gui.Presentation;

namespace SaveTether.Gui;

/// <summary>
/// The Backups page (desktop-app-spec.md, section 9.7), in place of the Apps page while open: backups grouped
/// by mapping with an app filter, Open folder, Restore and Delete. The logic is <see cref="BackupsPage"/>; restore
/// runs through <see cref="ActionFlow.RestoreAsync"/> as an operation, after which the list is read again.
/// </summary>
internal sealed class BackupsScreen : UserControl, IScreen
{
    /// <summary>The way back, shown by the window above the scrolling page.</summary>
    public ContentControl Bar { get; } = new();

    // The app filter sits beside the way back, above the scrolling list; it is known once the backups are read.
    private readonly ContentControl _filterHost = new() { VerticalAlignment = VerticalAlignment.Center };

    private readonly LocalStore _local;
    private readonly Func<Func<ActionFlow, Task>, Task> _operate;
    private readonly Action _close;
    private readonly ContentControl _page = new();
    private string? _appFilter;

    /// <param name="appFilter">An app to show at first, from a row's "Backups"; null for all.</param>
    public BackupsScreen(string localFolder, Func<Func<ActionFlow, Task>, Task> operate, Action close, string? appFilter)
    {
        _local = new LocalStore(localFolder);
        _operate = operate;
        _close = close;
        Bar.Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Backups", FontSize = 22, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = BackupsPage.Intro, TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    Children = { Navigation.BackRow("Back to apps", close), _filterHost },
                },
            },
        };
        _appFilter = appFilter;
        Content = new StackPanel { Spacing = 16, Children = { _page } };
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        _page.IsEnabled = false;
        IReadOnlyList<BackupGroup> groups;
        Func<string, string> appName;
        try
        {
            (groups, appName) = await Task.Run(() => BackupsPage.Load(_local));
        }
        catch (Exception e)
        {
            // The device cannot be read now (section 4.1's banners say why), or something unexpected: said here, logged.
            var expected = e is StoreException or IOException or UnauthorizedAccessException;
            App.Options.Log.Write(expected ? $"backup list: {e.GetType().Name}: {e.Message}" : $"unexpected error: {e}");
            _page.IsEnabled = true;
            _filterHost.Content = null;
            _page.Content = Page([new TextBlock { Text = expected ? e.Message : $"Something went wrong: {e.Message}", TextWrapping = TextWrapping.Wrap }]);
            return;
        }

        _page.IsEnabled = true;
        Show(groups, appName);
    }

    private void Show(IReadOnlyList<BackupGroup> groups, Func<string, string> appName)
    {
        var content = new List<Control>();
        _filterHost.Content = null;
        var apps = BackupsPage.Apps(groups, appName);
        if (_appFilter is not null && apps.All(a => a.AppId != _appFilter))
        {
            _appFilter = null;
        }

        if (apps.Count > 1)
        {
            var names = new List<string> { "All apps" };
            names.AddRange(apps.Select(a => a.Name));
            var filter = new ComboBox
            {
                ItemsSource = names,
                SelectedIndex = _appFilter is null ? 0 : 1 + apps.ToList().FindIndex(a => a.AppId == _appFilter),
                MinWidth = 220,
            };
            AutomationProperties.SetName(filter, "Show backups of");
            filter.SelectionChanged += (_, _) =>
            {
                _appFilter = filter.SelectedIndex <= 0 ? null : apps[filter.SelectedIndex - 1].AppId;
                Show(groups, appName);
            };
            _filterHost.Content = filter;
        }

        var shown = BackupsPage.Filter(groups, _appFilter);
        if (shown.Count == 0)
        {
            content.Add(new TextBlock { Text = "No backups yet. SaveTether takes one before it first changes a folder.", Opacity = 0.75 });
        }

        foreach (var group in shown)
        {
            content.Add(Navigation.GroupHeader(group.Name));
            foreach (var row in group.Rows)
            {
                content.Add(Row(group, row, appName));
            }
        }

        if (shown.Any(g => g.Rows.Any(r => r.Kept)))
        {
            content.Add(new TextBlock { Text = $"* {BackupsPage.KeptText}", Opacity = 0.7, FontSize = 13, Margin = new Thickness(0, 8, 0, 0) });
        }

        _page.Content = Page(content);
    }

    private Grid Row(BackupGroup group, BackupRow row, Func<string, string> appName)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("150,150,120,*,Auto,Auto,Auto"), Margin = new Thickness(0, 3) };
        Add(grid, 0, new TextBlock { Text = row.Kept ? $"{row.When} *" : row.When, VerticalAlignment = VerticalAlignment.Center });
        Add(grid, 1, new TextBlock { Text = row.Reason, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.85 });
        Add(grid, 2, new TextBlock { Text = row.Of, VerticalAlignment = VerticalAlignment.Center, FontWeight = row.OfSharedCopy ? FontWeight.SemiBold : FontWeight.Normal });
        Add(grid, 3, new TextBlock { Text = row.Size, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.85 });

        var open = new Button { Content = "Open folder", Margin = new Thickness(12, 0, 0, 0) };
        AutomationProperties.SetName(open, $"Open folder: {group.Name} backup {row.When}");
        open.Click += (_, _) => FolderOpener.Open(row.DataFolder, App.Options.Log);
        Add(grid, 4, open);

        var restore = new Button { Content = "Restore", Margin = new Thickness(8, 0, 0, 0) };
        AutomationProperties.SetName(restore, $"Restore: {group.Name} backup {row.When}");
        var role = group.MappingId.Contains('.', StringComparison.Ordinal) ? group.MappingId[(group.MappingId.IndexOf('.', StringComparison.Ordinal) + 1)..] : group.MappingId;
        restore.Click += (_, _) => _ = RestoreAsync(row, appName(group.AppId), role);
        Add(grid, 5, restore);

        var delete = new Button { Content = "Delete", Margin = new Thickness(8, 0, 0, 0) };
        AutomationProperties.SetName(delete, $"Delete: {group.Name} backup {row.When}");
        delete.Click += (_, _) => _ = DeleteAsync(row);
        Add(grid, 6, delete);
        return grid;
    }

    private async Task DeleteAsync(BackupRow row)
    {
        var deleted = false;
        await _operate(async actions => deleted = await actions.DeleteBackupAsync(row));
        if (deleted)
        {
            await LoadAsync();
        }
    }

    private async Task RestoreAsync(BackupRow row, string app, string role)
    {
        await _operate(actions => actions.RestoreAsync(row, app, role));

        // A restore takes a backup of what it replaced, so the list has changed.
        await LoadAsync();
    }

    private static StackPanel Page(IEnumerable<Control> content)
    {
        var page = new StackPanel { Spacing = 8 };
        foreach (var control in content)
        {
            page.Children.Add(control);
        }

        return page;
    }

    private static void Add(Grid grid, int column, Control control)
    {
        Grid.SetColumn(control, column);
        grid.Children.Add(control);
    }
}
