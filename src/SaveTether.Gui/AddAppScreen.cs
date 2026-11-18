using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SaveTether.Core.Resolution;
using SaveTether.Core.Storage;
using SaveTether.Gui.Presentation;

namespace SaveTether.Gui;

/// <summary>
/// Add app's two pages (desktop-app-spec.md, section 8), in place of the Apps page while open. The logic is
/// <see cref="AddAppFlow"/>; this only shows it. Planning reads only and runs off the UI thread; Add is an
/// operation, and after it "Apply it now?" runs the Apply flow for each role added.
/// </summary>
internal sealed class AddAppScreen : UserControl
{
    private readonly AddAppFlow _flow;
    private readonly Overlay _overlay;
    private readonly Func<Func<ActionFlow, Task>, Task> _operate;
    private readonly Action _close;
    private readonly ContentControl _page = new();
    private readonly Dictionary<string, TextBlock> _labels = new(StringComparer.Ordinal);
    private bool _closed;

    /// <param name="startAt">An app to open page 2 at, from a row's "Find the app again" or "Add"; null for page 1.</param>
    public AddAppScreen(string localFolder, Overlay overlay, Func<Func<ActionFlow, Task>, Task> operate, Action close, string? startAt)
    {
        _flow = new AddAppFlow(new LocalStore(localFolder), ResolverEnvironment.FromSystem());
        _overlay = overlay;
        _operate = operate;
        _close = close;
        Content = new StackPanel { Spacing = 16, MaxWidth = 820, HorizontalAlignment = HorizontalAlignment.Left, Children = { _page } };
        if (startAt is null)
        {
            _ = ShowCatalogAsync();
        }
        else
        {
            _ = PlanAsync(() => _flow.Start(startAt));
        }
    }

    /// <summary>Stops the background detection when the screen is left.</summary>
    public void Leave()
    {
        _closed = true;
        _close();
    }

    // ---- Page 1 ----

    private async Task ShowCatalogAsync()
    {
        var apps = await Guard(() => _flow.Catalog());
        if (apps is null)
        {
            return;
        }

        var search = new TextBox { PlaceholderText = "Search", Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(search, "Search apps");
        var list = new StackPanel { Spacing = 2 };
        _labels.Clear();
        var rows = new Dictionary<string, Control>(StringComparer.Ordinal);
        foreach (var app in apps)
        {
            var status = new TextBlock { Text = "Looking…", Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center };
            _labels[app.Id] = status;
            var source = app.Source == Core.Definitions.DefinitionSource.Shared ? "shared definition" : "built in";
            var open = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,160,120"),
                    Children =
                    {
                        Column(new TextBlock { Text = app.Name, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }, 0),
                        Column(new TextBlock { Text = source, Opacity = 0.65, FontSize = 13, VerticalAlignment = VerticalAlignment.Center }, 1),
                        Column(status, 2),
                    },
                },
            };
            AutomationProperties.SetName(open, app.Name);
            open.Click += (_, _) => _ = PlanAsync(() => _flow.Start(app.Id));
            rows[app.Id] = open;
            list.Children.Add(open);
        }

        search.TextChanged += (_, _) =>
        {
            var shown = AddAppFlow.Filter(apps, search.Text).Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var (id, row) in rows)
            {
                row.IsVisible = shown.Contains(id);
            }
        };

        var back = new Button { Content = "Back to apps" };
        back.Click += (_, _) => Leave();
        _page.Content = Page(
            "Add app",
            ["Choose an app. SaveTether looks for it on this device and shows what it would share before anything is written."],
            search,
            list,
            Buttons(back));
        search.Focus();

        // Filled in the background: opening the page does not wait for detection (section 8).
        var labels = _labels.ToList();
        _ = Task.Run(() =>
        {
            foreach (var (id, label) in labels)
            {
                if (_closed)
                {
                    return;
                }

                string text;
                try
                {
                    text = AddAppFlow.Label(_flow.Detect(id));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or StoreException)
                {
                    text = "Cannot tell";
                }

                Dispatcher.UIThread.Post(() => label.Text = text);
            }
        });
    }

    // ---- Page 2 ----

    private async Task PlanAsync(Func<AddAppView> plan)
    {
        _page.IsEnabled = false;
        var view = await Guard(plan);
        _page.IsEnabled = true;
        if (view is not null)
        {
            Show(view);
        }
    }

    private void Show(AddAppView view)
    {
        var back = new Button { Content = "Back" };
        back.Click += (_, _) =>
        {
            if (view.Page != AddAppPage.Plan && _flow.Back() is { } last)
            {
                Show(last);
            }
            else
            {
                _ = ShowCatalogAsync();
            }
        };

        var content = new List<Control>();
        switch (view.Page)
        {
            case AddAppPage.Plan:
                content.AddRange(PlanContent(view));
                var add = new Button { Content = "Add", IsDefault = true, IsEnabled = view.CanAdd };
                add.Click += (_, _) => _ = AddAsync(view);
                content.Add(Buttons(back, add));
                break;

            case AddAppPage.ChooseInstall:
                foreach (var candidate in view.Candidates)
                {
                    var use = new Button { Content = $"Use {candidate.Id}" };
                    use.Click += (_, _) => _ = PlanAsync(() => _flow.ChooseInstall(candidate.Id));
                    content.Add(new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 12,
                        Children = { use, new TextBlock { Text = candidate.Found ?? string.Empty, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center } },
                    });
                }

                content.Add(Buttons(back));
                break;

            case AddAppPage.NeedsLocation:
                var fields = new List<(LocationField Field, TextBox Box)>();
                foreach (var (field, value) in view.Fields)
                {
                    var (box, row) = PathField(value ?? string.Empty, AddAppFlow.FieldLabel(field));
                    fields.Add((field, box));
                    content.Add(new TextBlock { Text = AddAppFlow.FieldLabel(field), FontWeight = FontWeight.SemiBold });
                    content.Add(row);
                }

                var details = view.Details is { } d ? Overlay.Details(d) : null;
                if (details is not null)
                {
                    content.Add(details);
                }

                if (fields.Count > 0)
                {
                    var look = new Button { Content = "Look again", IsDefault = true };
                    look.Click += (_, _) =>
                    {
                        // Read here, on the UI thread: a control cannot be read from the planning thread (found on the Deck).
                        var typed = fields.Select(f => (f.Field, Text: f.Box.Text)).ToList();
                        _ = PlanAsync(() =>
                        {
                            AddAppView? last = null;
                            foreach (var (field, text) in typed)
                            {
                                last = _flow.SetLocation(field, text);
                            }

                            return last!;
                        });
                    };
                    content.Add(Buttons(back, look));
                }
                else
                {
                    content.Add(Buttons(back));
                }

                break;

            default:
                if (view.Details is { } why)
                {
                    content.Add(Overlay.Details(why));
                }

                content.Add(Buttons(back));
                break;
        }

        _page.Content = Page(view.Title, view.Sentences, [.. content]);
    }

    private IEnumerable<Control> PlanContent(AddAppView view)
    {
        if (view.Found is { } found)
        {
            yield return new TextBlock { Text = found, TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
        }

        yield return new TextBlock { Text = "Folders to share", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var role in view.Roles)
        {
            var lines = new StackPanel { Spacing = 2 };
            lines.Children.Add(new TextBlock { Text = $"{role.RoleId} ({AddAppFlow.ClassName(role.Class)})", FontWeight = FontWeight.SemiBold });
            if (role.Planned is { } planned)
            {
                lines.Children.Add(new TextBlock { Text = $"{planned.Source}: {AddAppFlow.SizeOf(planned)}", TextWrapping = TextWrapping.Wrap, Opacity = 0.8, FontSize = 13 });
            }

            if (role.Unticked is { } off)
            {
                lines.Children.Add(new TextBlock { Text = off, Opacity = 0.75, FontSize = 13 });
            }

            if (role.Notes is { } notes)
            {
                lines.Children.Add(new TextBlock { Text = notes, TextWrapping = TextWrapping.Wrap, Opacity = 0.8, FontSize = 13 });
            }

            var box = new CheckBox { IsChecked = role.Ticked, Content = lines };
            AutomationProperties.SetName(box, $"Share {role.RoleId}");
            var roleId = role.RoleId;
            box.IsCheckedChanged += (_, _) =>
            {
                var ticked = box.IsChecked == true;
                _ = PlanAsync(() => _flow.SetRole(roleId, ticked));
            };
            yield return box;
        }

        if (!view.CanAdd)
        {
            yield return new TextBlock { Text = "Tick at least one folder to add.", Foreground = new SolidColorBrush(Icons.Amber) };
        }

        if (view.Quirks.Count > 0)
        {
            yield return new TextBlock { Text = "Worth knowing", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) };
            foreach (var quirk in view.Quirks)
            {
                yield return new TextBlock { Text = quirk, TextWrapping = TextWrapping.Wrap };
            }
        }

        yield return new TextBlock
        {
            Text = "Adding records these folders; it changes no saves. Apply links them afterwards.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            Margin = new Thickness(0, 8, 0, 0),
        };
    }

    /// <summary>Add, then "Apply it now?" (section 8). The plan page was the confirmation.</summary>
    private Task AddAsync(AddAppView view) => _operate(async actions =>
    {
        _page.IsEnabled = false;
        var result = await Guard(() => _flow.Add(view));
        _page.IsEnabled = true;
        if (result is null)
        {
            return;
        }

        if (result.Changed is { } changed)
        {
            Show(changed);
            return;
        }

        if (!result.Succeeded)
        {
            App.Options.Log.Write($"app add {view.AppId} refused: {result.Refused}");
            await _overlay.InformAsync("Nothing was changed", result.Refused ?? "The app could not be added.");
            return;
        }

        App.Options.Log.Write($"app add {view.AppId}: added {string.Join(", ", result.Added)}");
        Leave();
        var notNow = new Button { Content = "Not now" };
        var apply = new Button { Content = "Apply" };
        var pressed = await _overlay.ShowAsync(AddAppFlow.AddedQuestion(view.AppName), Overlay.Body(AddAppFlow.AddedText(result.Added)), [notNow, apply], notNow);
        if (pressed == apply)
        {
            await actions.ApplyAddedAsync(view.AppName, result.Added);
        }
    });

    /// <summary>
    /// Runs a read or Add off the UI thread. A refusal from the core is shown on the page instead of ending the
    /// flow. Anything else is section 10's unexpected error: logged with its stack and said, since the callers
    /// do not await this and an exception would otherwise vanish with the page left disabled (found on the Deck).
    /// </summary>
    private async Task<T?> Guard<T>(Func<T> work)
        where T : class
    {
        try
        {
            return await Task.Run(work);
        }
        catch (Exception e)
        {
            var expected = e is StoreException or IOException or UnauthorizedAccessException;
            App.Options.Log.Write(expected ? $"add app: {e.GetType().Name}: {e.Message}" : $"unexpected error: {e}");
            var back = new Button { Content = "Back to apps" };
            back.Click += (_, _) => Leave();
            _page.IsEnabled = true;
            _page.Content = expected
                ? Page("Add app cannot go on", [e.Message], Buttons(back))
                : Page("Something went wrong", [$"Something went wrong: {e.Message}", "SaveTether is still running. The details are in gui.log."], Buttons(back));
            return null;
        }
    }

    private (TextBox Field, Control Row) PathField(string value, string name)
    {
        var field = new TextBox { Text = value, Width = 520 };
        AutomationProperties.SetName(field, name);
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

    private static StackPanel Page(string title, IReadOnlyList<string> sentences, params Control[] content)
    {
        var page = new StackPanel { Spacing = 10 };
        page.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        foreach (var sentence in sentences)
        {
            page.Children.Add(new TextBlock { Text = sentence, TextWrapping = TextWrapping.Wrap });
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

    private static Control Column(Control control, int column)
    {
        Grid.SetColumn(control, column);
        return control;
    }
}
