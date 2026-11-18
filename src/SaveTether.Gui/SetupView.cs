using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using SaveTether.Core.Storage;
using SaveTether.Gui.Presentation;

namespace SaveTether.Gui;

/// <summary>
/// First-run setup's three pages (desktop-app-spec.md, section 6). The logic is <see cref="SetupFlow"/>;
/// this only shows it. Nothing is written until Set up on the last page.
/// </summary>
internal sealed class SetupView : UserControl
{
    private readonly SetupFlow _flow;
    private readonly Overlay _overlay;
    private readonly Action _done;
    private readonly ContentControl _page = new();
    private readonly TextBlock _stepLabel = new() { Opacity = 0.75 };

    public SetupView(string localFolder, Overlay overlay, Action done)
    {
        _flow = new SetupFlow(new LocalStore(localFolder), Path.Combine(localFolder, "backups"), Environment.MachineName);
        _overlay = overlay;
        _done = done;
        Content = new StackPanel
        {
            Spacing = 16,
            MaxWidth = 760,
            HorizontalAlignment = HorizontalAlignment.Left,
            Children =
            {
                new TextBlock { Text = "Set up this device", FontSize = 22, FontWeight = FontWeight.SemiBold },
                _stepLabel,
                _page,
            },
        };
        ShowSyncFolder();
    }

    private void ShowSyncFolder(string? error = null)
    {
        _stepLabel.Text = "Step 1 of 3: the sync folder";
        var path = PathField(_flow.SyncFolder?.Path ?? string.Empty, "Sync folder");
        var finding = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var firstDevice = new Button { Content = "This is my first device", IsVisible = false };
        var next = new Button { Content = "Next", IsDefault = true };

        void Check()
        {
            var check = _flow.CheckSyncFolder(path.Field.Text ?? string.Empty);
            finding.Text = check.Sentence;
            firstDevice.IsVisible = check.OffersFirstDevice;
            if (check.CanContinue)
            {
                ShowDeviceName();
            }
        }

        next.Click += (_, _) => Check();
        firstDevice.Click += async (_, _) =>
        {
            if (await _overlay.ConfirmAsync("Is this the first device?", SetupFlow.FirstDeviceWarning, "This is my first device"))
            {
                _flow.ConfirmFirstDevice();
                ShowDeviceName();
            }
        };

        _page.Content = Page(
            "The folder your sync tool keeps in step on every device: Syncthing's folder, a OneDrive or Nextcloud folder, a share.",
            error,
            path.Row,
            finding,
            Buttons(firstDevice, next));
        path.Field.Focus();
    }

    private void ShowDeviceName(string? error = null)
    {
        _stepLabel.Text = "Step 2 of 3: this device's name";
        var children = new List<Control>();
        var existing = _flow.ExistingDevices();
        if (existing.Count > 0)
        {
            children.Add(new TextBlock { Text = "Set up again after a reinstall? Take over this device's old name:", TextWrapping = TextWrapping.Wrap });
            foreach (var device in existing)
            {
                var takeOver = new Button { Content = "Take over" };
                takeOver.Click += async (_, _) =>
                {
                    if (await _overlay.ConfirmAsync($"Take over '{device.Name}'?", SetupFlow.TakeOverWarning(device.Name), "Take over"))
                    {
                        _flow.ChooseTakeOver(device.Name);
                        ShowBackups();
                    }
                };
                children.Add(new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    Children = { new TextBlock { Text = device.Name, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, MinWidth = 160 }, takeOver },
                });
            }

            children.Add(new TextBlock { Text = "Or give it a new name:", Margin = new Thickness(0, 8, 0, 0) });
        }

        var name = new TextBox { Text = _flow.DeviceName is { } chosen && !_flow.TakeOver ? chosen : _flow.SuggestedName, Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationName(name, "Device name");
        var problem = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Icons.Amber) };
        var next = new Button { Content = "Next", IsDefault = true };

        void Validate()
        {
            var text = _flow.NameProblem(name.Text ?? string.Empty);
            problem.Text = text ?? string.Empty;
            next.IsEnabled = text is null;
        }

        name.TextChanged += (_, _) => Validate();
        next.Click += (_, _) =>
        {
            _flow.ChooseName(name.Text ?? string.Empty);
            ShowBackups();
        };
        var back = new Button { Content = "Back" };
        back.Click += (_, _) => ShowSyncFolder();

        children.Add(name);
        children.Add(new TextBlock { Text = SetupFlow.NameRule, Opacity = 0.75, FontSize = 13, TextWrapping = TextWrapping.Wrap });
        children.Add(problem);
        children.Add(Buttons(back, next));
        _page.Content = Page("Each device has its own name in the sync folder, such as 'deck' or 'desktop-pc'.", error, [.. children]);
        Validate();
        name.Focus();
    }

    private void ShowBackups(string? error = null)
    {
        _stepLabel.Text = "Step 3 of 3: backups";
        var path = PathField(_flow.SuggestedBackupRoot, "Backup location");
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Icons.Amber) };

        void Note() => note.Text = _flow.BackupRootNote(path.Field.Text ?? string.Empty) ?? string.Empty;

        path.Field.TextChanged += (_, _) => Note();
        var setUp = new Button { Content = "Set up", IsDefault = true };
        var back = new Button { Content = "Back" };
        back.Click += (_, _) => ShowDeviceName();
        setUp.Click += async (_, _) =>
        {
            setUp.IsEnabled = false;
            back.IsEnabled = false;
            var root = path.Field.Text ?? string.Empty;
            var outcome = await Task.Run(() => _flow.Finish(root));
            if (outcome.Succeeded)
            {
                App.Options.Log.Write($"setup: device '{outcome.Result!.DeviceName}' at '{outcome.Result.SyncRoot}'");
                _done();
                return;
            }

            App.Options.Log.Write($"setup refused at {outcome.FailedAt}: {outcome.Error}");
            switch (outcome.FailedAt)
            {
                case SetupStep.SyncFolder:
                    ShowSyncFolder(outcome.Error);
                    break;
                case SetupStep.DeviceName:
                    ShowDeviceName(outcome.Error);
                    break;
                default:
                    ShowBackups(outcome.Error);
                    break;
            }
        };

        var summary = $"This device: '{_flow.DeviceName}'{(_flow.TakeOver ? " (taken over)" : string.Empty)}, sync folder {_flow.SyncFolder?.Path}.";
        _page.Content = Page(
            "Before SaveTether changes a save folder, it copies it here. Keep it on this device, outside the sync folder.",
            error,
            path.Row,
            note,
            new TextBlock { Text = summary, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 },
            Buttons(back, setUp));
        Note();
        setUp.Focus();
    }

    private (TextBox Field, Control Row) PathField(string value, string name)
    {
        var field = new TextBox { Text = value, Width = 520 };
        AutomationName(field, name);
        var choose = new Button { Content = "Choose folder" };
        choose.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanPickFolder: true } storage)
            {
                return;
            }

            var picked = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = name, AllowMultiple = false });
            if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } local)
            {
                field.Text = local;
            }
        };

        return (field, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { field, choose } });
    }

    private static StackPanel Page(string intro, string? error, params Control[] content)
    {
        var page = new StackPanel { Spacing = 12 };
        page.Children.Add(new TextBlock { Text = intro, TextWrapping = TextWrapping.Wrap });
        if (error is not null)
        {
            page.Children.Add(new Border
            {
                Padding = new Thickness(12),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Icons.Amber),
                Background = new SolidColorBrush(Icons.Amber, 0.12),
                Child = new TextBlock { Text = error, TextWrapping = TextWrapping.Wrap },
            });
        }

        foreach (var control in content)
        {
            page.Children.Add(control);
        }

        return page;
    }

    private static StackPanel Buttons(params Button[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var button in buttons)
        {
            row.Children.Add(button);
        }

        return row;
    }

    private static void AutomationName(Control control, string name) => Avalonia.Automation.AutomationProperties.SetName(control, name);
}
