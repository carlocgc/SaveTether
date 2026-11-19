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

/// <summary>Where Add app opens: page 1, page 2 for an app, or the hand form for a mapping with no definition.</summary>
internal sealed record AddAppStart(string? AppId, string? MappingId)
{
    public static AddAppStart Catalog { get; } = new(null, null);

    public static AddAppStart App(string appId) => new(appId, null);

    public static AddAppStart Folder(string mappingId) => new(null, mappingId);
}

/// <summary>
/// Add app's two pages (desktop-app-spec.md, section 8), in place of the Apps page while open. The logic is
/// <see cref="AddAppFlow"/>; this only shows it. Planning reads only and runs off the UI thread; Add is an
/// operation, and after it "Apply it now?" runs the Apply flow for each role added.
/// </summary>
internal sealed class AddAppScreen : UserControl
{
    private readonly AddAppFlow _flow;
    private readonly ManualAddFlow _manual;
    private readonly Overlay _overlay;
    private readonly Func<Func<ActionFlow, Task>, Task> _operate;
    private readonly Action _close;
    private readonly ContentControl _page = new();
    private readonly Dictionary<string, TextBlock> _labels = new(StringComparer.Ordinal);
    private bool _closed;

    /// <param name="startAt">Page 1; page 2 for an app, from a row's "Find the app again" or "Add"; or the hand form for a mapping.</param>
    public AddAppScreen(string localFolder, Overlay overlay, Func<Func<ActionFlow, Task>, Task> operate, Action close, AddAppStart startAt)
    {
        ArgumentNullException.ThrowIfNull(startAt);
        var local = new LocalStore(localFolder);
        _flow = new AddAppFlow(local, ResolverEnvironment.FromSystem());
        _manual = new ManualAddFlow(local);
        _overlay = overlay;
        _operate = operate;
        _close = close;
        Content = new StackPanel { Spacing = 16, MaxWidth = 820, HorizontalAlignment = HorizontalAlignment.Left, Children = { _page } };
        if (startAt.AppId is { } appId)
        {
            _ = PlanAsync(() => _flow.Start(appId));
        }
        else if (startAt.MappingId is { } mappingId)
        {
            _ = ShowManualAsync(() => _manual.StartJoin(mappingId), fromCatalog: false);
        }
        else
        {
            _ = ShowCatalogAsync();
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

        // Decision 6: the way to add an app that is not in the list, without the CLI.
        var byHand = new HyperlinkButton { Content = "Add a folder by hand", Padding = new Thickness(0) };
        AutomationProperties.SetName(byHand, "Add a folder by hand, for an app not in the list");
        byHand.Click += (_, _) => _ = ShowManualAsync(() => _manual.StartNew(), fromCatalog: true);
        _page.Content = Page(
            Navigation.BackRow("Back to apps", Leave),
            "Add app",
            ["Choose an app. SaveTether looks for it on this device and shows what it would share before anything is written."],
            search,
            list,
            new TextBlock { Text = "Not in the list?", Opacity = 0.75, Margin = new Thickness(0, 8, 0, 0) },
            byHand);
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
        var back = Navigation.BackRow("Back", () =>
        {
            if (view.Page != AddAppPage.Plan && _flow.Back() is { } last)
            {
                Show(last);
            }
            else
            {
                _ = ShowCatalogAsync();
            }
        });

        var content = new List<Control>();
        switch (view.Page)
        {
            case AddAppPage.Plan:
                content.AddRange(PlanContent(view));
                var add = new Button { Content = "Add", IsDefault = true, IsEnabled = view.CanAdd };
                add.Click += (_, _) => _ = AddAsync(view);
                content.Add(Buttons(add));
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
                    content.Add(Buttons(look));
                }

                break;

            default:
                if (view.Details is { } why)
                {
                    content.Add(Overlay.Details(why));
                }

                break;
        }

        _page.Content = Page(back, view.Title, view.Sentences, [.. content]);
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

    // ---- Add a folder by hand ----

    private async Task ShowManualAsync(Func<ManualAddStart> start, bool fromCatalog)
    {
        _page.IsEnabled = false;
        var form = await Guard(start);
        _page.IsEnabled = true;
        if (form is not null)
        {
            ShowManual(form, form.Input, null, fromCatalog);
        }
    }

    /// <summary>
    /// The hand form (section 8, decision 6): the folder to share, and for a new folder the app's and the folder's names,
    /// from which the mapping ID and the shared folder follow; process names and entry depth. Problems found by Add
    /// are shown beside their fields.
    /// </summary>
    private void ShowManual(ManualAddStart form, ManualAddInput input, ManualAddCheck? check, bool fromCatalog)
    {
        var content = new List<Control>();
        Control Problems(ManualField field)
        {
            var problems = new StackPanel { Spacing = 2 };
            foreach (var (_, problem) in check?.Problems.Where(p => p.Field == field) ?? [])
            {
                problems.Children.Add(new TextBlock { Text = problem, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Icons.Amber) });
            }

            return problems;
        }

        void Label(string text, string? hint = null)
        {
            content.Add(new TextBlock { Text = text, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
            if (hint is not null)
            {
                content.Add(new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, Opacity = 0.75, FontSize = 13 });
            }
        }

        Label("Folder to share", "The folder on this device: saves, mods, textures or anything else the app keeps in one folder.");
        var (folder, folderRow) = PathField(input.SaveFolder ?? string.Empty, "Folder to share");
        content.Add(folderRow);
        content.Add(Problems(ManualField.SaveFolder));

        TextBox? appName = null;
        TextBox? savesName = null;
        NumericUpDown? depth = null;
        if (form.IsJoin)
        {
            content.Add(new TextBlock { Text = $"Shared as {form.Target}.", TextWrapping = TextWrapping.Wrap, Opacity = 0.85, Margin = new Thickness(0, 6, 0, 0) });
        }
        else
        {
            Label("App name", "For example Dolphin. Every folder of one app shares this name.");
            appName = new TextBox { Text = input.AppName ?? string.Empty, Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetName(appName, "App name");
            content.Add(appName);
            content.Add(Problems(ManualField.AppName));

            Label("Name for the shared folder", "The folder's own name is usually right, for example GC.");
            savesName = new TextBox { Text = input.FolderName ?? string.Empty, Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetName(savesName, "Name for the shared folder");
            content.Add(savesName);
            content.Add(Problems(ManualField.FolderName));

            var sharedAs = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
            void Update()
            {
                var (id, target) = ManualAddFlow.Derive(appName.Text, savesName.Text);
                sharedAs.Text = ManualAddFlow.SharedAs(id, target);
            }

            appName.TextChanged += (_, _) => Update();
            savesName.TextChanged += (_, _) => Update();
            // The shared folder's name follows the folder until the owner types their own.
            var lastSuggested = string.IsNullOrEmpty(input.FolderName) ? string.Empty : null;
            folder.TextChanged += (_, _) =>
            {
                var next = ManualAddFlow.FollowFolder(savesName.Text, lastSuggested, folder.Text);
                if (!string.Equals(next, savesName.Text, StringComparison.Ordinal))
                {
                    lastSuggested = next;
                    savesName.Text = next;
                }
            };
            Update();
            content.Add(sharedAs);
        }

        Label("Process names (optional)", "The app's process name, as Task Manager or System Monitor shows it, for example Dolphin. Apply, Revert and Restore wait while one of these runs. Separate several with commas.");
        var processes = new TextBox { Text = input.ProcessNames ?? string.Empty, Width = 520, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(processes, "Process names");
        var pick = new Button { Content = "Choose running app" };
        AutomationProperties.SetName(pick, "Choose a process name from the running apps");
        pick.Click += (_, _) => _ = PickProcessAsync(processes);
        content.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { processes, pick } });
        content.Add(Problems(ManualField.ProcessNames));

        if (form.IsJoin || check?.ExistingDepth is not null)
        {
            var shown = check?.ExistingDepth ?? input.EntryDepth ?? 1;
            if (shown > 1)
            {
                content.Add(new TextBlock { Text = $"Entry depth {shown}, as set when the folder was first added.", Opacity = 0.75, FontSize = 13 });
            }
        }
        else
        {
            // Under Advanced, closed: 1 is right unless items sit deeper, as in Cemu's save folder (design 5.13).
            depth = new NumericUpDown
            {
                Value = input.EntryDepth ?? 1, Minimum = 1, Maximum = MappingFileMaxDepth, Increment = 1, FormatString = "0", Width = 140,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            AutomationProperties.SetName(depth, "Entry depth");
            var advanced = new StackPanel { Spacing = 6 };
            advanced.Children.Add(new TextBlock { Text = "Entry depth", FontWeight = FontWeight.SemiBold });
            advanced.Children.Add(new TextBlock
            {
                Text = "When you link a folder that already holds files, and the shared folder has files too (for example Cemu saves on "
                    + "a second PC), Apply can add the folders the shared copy doesn't have. Entry depth says how deep to look for them: "
                    + "1 when each game or mod is a folder directly inside, 2 when they sit one level down, as in Cemu's save folder "
                    + "(title group, then title). A folder the shared copy already has is left as it is.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75,
                FontSize = 13,
            });
            advanced.Children.Add(depth);
            advanced.Children.Add(Problems(ManualField.EntryDepth));
            var hasProblem = check?.Problems.Any(p => p.Field == ManualField.EntryDepth) == true;
            content.Add(new Expander
            {
                Header = "Advanced",
                Content = advanced,
                IsExpanded = hasProblem || (input.EntryDepth ?? 1) != 1,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 8, 0, 0),
            });
        }

        foreach (var note in check?.Notes ?? [])
        {
            content.Add(new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, Opacity = 0.85 });
        }

        content.Add(new TextBlock
        {
            Text = "Adding only records this folder; nothing in it changes. Apply links it afterwards.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            Margin = new Thickness(0, 8, 0, 0),
        });

        var back = Navigation.BackRow(fromCatalog ? "Back" : "Back to apps", () =>
        {
            if (fromCatalog)
            {
                _ = ShowCatalogAsync();
            }
            else
            {
                Leave();
            }
        });
        var add = new Button { Content = "Add", IsDefault = true };
        add.Click += (_, _) =>
        {
            // Read here, on the UI thread: a control cannot be read from another thread (found on the Deck).
            var typed = new ManualAddInput(
                folder.Text, appName?.Text ?? input.AppName, savesName?.Text ?? input.FolderName, processes.Text,
                depth is null ? input.EntryDepth : (int?)depth.Value);
            _ = ManualAddAsync(form, typed, fromCatalog);
        };
        content.Add(Buttons(add));

        _page.Content = Page(back, form.Title, form.Sentences, [.. content]);
        // Focus once the page is laid out: focused at once, the field did not take it when the form was opened from
        // a row (found on the Deck), and Return then pressed Add with the old folder still in the field.
        Dispatcher.UIThread.Post(() => folder.Focus(), DispatcherPriority.Loaded);
    }

    private const int MappingFileMaxDepth = Core.Model.MappingFile.MaxEntryDepth;

    /// <summary>
    /// The process picker (section 8): what is running, read by the running-app check's own code, with a search.
    /// The name chosen is added to the field; the owner can still edit it there.
    /// </summary>
    private async Task PickProcessAsync(TextBox field)
    {
        IReadOnlyList<string> names;
        try
        {
            names = await Task.Run(() => ProcessChoices.Running(new Core.Planning.SystemProcessLister()));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            App.Options.Log.Write($"process list: {e.GetType().Name}: {e.Message}");
            await _overlay.InformAsync("Running apps cannot be listed", e.Message);
            return;
        }

        var search = new TextBox { PlaceholderText = "Search", Width = 380, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(search, "Search running processes");
        var list = new ListBox { ItemsSource = names, Height = 280, Width = 380, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(list, "Running processes");
        var cancel = new Button { Content = "Cancel" };
        var add = new Button { Content = "Add", IsEnabled = false };
        search.TextChanged += (_, _) => list.ItemsSource = ProcessChoices.Filter(names, search.Text);
        list.SelectionChanged += (_, _) => add.IsEnabled = list.SelectedItem is string;
        // A double click on a name adds it, as Add would.
        list.DoubleTapped += (_, _) =>
        {
            if (list.SelectedItem is string && add.IsEffectivelyEnabled)
            {
                add.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            }
        };

        var body = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Start the app first if it is not running, then choose it here.", TextWrapping = TextWrapping.Wrap },
                search,
                list,
            },
        };

        var shown = _overlay.ShowAsync("Choose a running app", body, [cancel, add], cancel);
        // The dialog focuses Cancel once its buttons answer; the search box is where the owner starts, so it takes the focus after that.
        DispatcherTimer.RunOnce(() => search.Focus(), Pacing.DialogMinimum + TimeSpan.FromMilliseconds(50));
        if (await shown == add && list.SelectedItem is string name)
        {
            field.Text = ProcessChoices.Add(field.Text, name);
        }
    }

    /// <summary>Add from the hand form, then "Apply it now?" as for an app (section 8).</summary>
    private Task ManualAddAsync(ManualAddStart form, ManualAddInput input, bool fromCatalog) => _operate(async actions =>
    {
        _page.IsEnabled = false;
        var result = await Guard(() => _manual.Add(input, joinAccepted: false));
        if (result?.Check is { JoinsExisting: true, CanAdd: true } joins)
        {
            _page.IsEnabled = true;
            var cancel = new Button { Content = "Cancel" };
            var share = new Button { Content = "Share it from here" };
            if (await _overlay.ShowAsync("Already shared by another device", Overlay.Body(ManualAddFlow.JoinQuestion(joins)), [cancel, share], cancel) != share)
            {
                ShowManual(form, input, joins, fromCatalog);
                return;
            }

            _page.IsEnabled = false;
            result = await Guard(() => _manual.Add(input, joinAccepted: true));
        }

        _page.IsEnabled = true;
        if (result is null)
        {
            return;
        }

        if (result.Check is { } check)
        {
            ShowManual(form, input, check, fromCatalog);
            return;
        }

        if (!result.Succeeded)
        {
            App.Options.Log.Write($"map add refused: {result.Refused}");
            await _overlay.InformAsync("Nothing was changed", result.Refused ?? "The folder could not be added.");
            return;
        }

        App.Options.Log.Write($"map add {result.Added}: added by hand");
        Leave();
        var name = form.IsJoin ? result.Added! : input.AppName!.Trim();
        var notNow = new Button { Content = "Not now" };
        var apply = new Button { Content = "Apply" };
        var pressed = await _overlay.ShowAsync(AddAppFlow.AddedQuestion(name), Overlay.Body(AddAppFlow.AddedText([result.Added!])), [notNow, apply], notNow);
        if (pressed == apply)
        {
            await actions.ApplyAddedAsync(name, [result.Added!]);
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
            var back = Navigation.BackRow("Back to apps", Leave);
            _page.IsEnabled = true;
            _page.Content = expected
                ? Page(back, "Add app cannot go on", [e.Message])
                : Page(back, "Something went wrong", [$"Something went wrong: {e.Message}", "SaveTether is still running. The details are in gui.log."]);
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

    /// <summary>A page: the way back at the top, then its title, sentences and content.</summary>
    private static StackPanel Page(Control back, string title, IReadOnlyList<string> sentences, params Control[] content)
    {
        var page = new StackPanel { Spacing = 10 };
        page.Children.Add(back);
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
