using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace SaveTether.Gui;

/// <summary>
/// Dialogs inside the main window (desktop-app-spec.md, principles 5 and 6): the page is dimmed and cannot
/// take focus, the dialog opens with focus on the choice that changes nothing, and Escape picks that choice.
/// </summary>
internal sealed class Overlay : Panel
{
    /// <summary>A dialog left open during an operation is cancelled after this, releasing the lock (decision 5).</summary>
    public static readonly TimeSpan OperationDialogTimeout = TimeSpan.FromMinutes(10);

    private readonly Control _page;

    public Overlay(Control page)
    {
        _page = page;
        Children.Add(page);
    }

    /// <summary>Tells something that needs no choice; closed with OK or Escape.</summary>
    public async Task InformAsync(string title, string text, Control? details = null)
    {
        var ok = new Button { Content = "OK" };
        await ShowAsync(title, Body(text, details), [ok], ok);
    }

    /// <summary>Asks, and answers true only for the confirming button.</summary>
    public async Task<bool> ConfirmAsync(string title, string text, string confirmLabel, TimeSpan? timeout = null, Control? details = null)
    {
        var cancel = new Button { Content = "Cancel" };
        var confirm = new Button { Content = confirmLabel };
        return await ShowAsync(title, Body(text, details), [cancel, confirm], cancel, timeout) == confirm;
    }

    /// <summary>
    /// Shows a dialog with the given content and buttons, and answers the button pressed. Escape, and the
    /// timeout when given, answer <paramref name="safe"/>, which also has the focus when the dialog opens.
    /// </summary>
    public Task<Button> ShowAsync(string title, Control body, IReadOnlyList<Button> buttons, Button safe, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(buttons);
        var answer = new TaskCompletionSource<Button>();
        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var button in buttons)
        {
            buttonRow.Children.Add(button);
        }

        var layer = Layer(title, body, buttonRow);
        var window = TopLevel.GetTopLevel(this);
        DispatcherTimer? timer = null;

        // Escape is caught at the window, on the way down, so it works wherever focus is: disabling the page
        // can leave focus on nothing, and then a key event never passes through the dialog. Found on the Deck.
        void OnKey(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close(safe);
            }
        }

        void Close(Button pressed)
        {
            if (answer.Task.IsCompleted)
            {
                return;
            }

            timer?.Stop();
            window?.RemoveHandler(KeyDownEvent, OnKey);
            Children.Remove(layer);
            _page.IsEnabled = Children.Count == 1;
            answer.TrySetResult(pressed);
        }

        foreach (var button in buttons)
        {
            button.Click += (_, _) => Close(button);
        }

        window?.AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        if (timeout is { } after)
        {
            timer = new DispatcherTimer(after, DispatcherPriority.Normal, (_, _) => Close(safe));
            timer.Start();
        }

        _page.IsEnabled = false;
        Children.Add(layer);

        // Focus moves to the safe button once the dialog is laid out; before that it cannot take focus.
        Dispatcher.UIThread.Post(() => safe.Focus(NavigationMethod.Tab), DispatcherPriority.Loaded);
        return answer.Task;
    }

    /// <summary>A dialog with no buttons that stays until the returned handle is disposed: an operation's progress.</summary>
    public IDisposable ShowBlocking(string title, Control body)
    {
        var layer = Layer(title, body, null);
        _page.IsEnabled = false;
        Children.Add(layer);
        return new Closer(() =>
        {
            Children.Remove(layer);
            _page.IsEnabled = Children.Count == 1;
        });
    }

    public static Control Body(string text, Control? details = null)
    {
        var body = new StackPanel { Spacing = 12, Children = { new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap } } };
        if (details is not null)
        {
            body.Children.Add(details);
        }

        return body;
    }

    /// <summary>The core's own words, collapsed under "Details" (principle 3: nothing the core knows is hidden).</summary>
    public static Expander Details(string text) => new()
    {
        Header = "Details",
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Content = new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13 },
    };

    private static Grid Layer(string title, Control body, Control? buttons)
    {
        var content = new StackPanel
        {
            Spacing = 16,
            Children = { new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap } },
        };
        content.Children.Add(new ScrollViewer { Content = body, MaxHeight = 420 });
        if (buttons is not null)
        {
            content.Children.Add(buttons);
        }

        var card = new Border
        {
            MaxWidth = 640,
            Margin = new Thickness(24),
            Padding = new Thickness(24),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark
                ? new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B))
                : Brushes.White,
            BorderBrush = new SolidColorBrush(Icons.Amber),
            BorderThickness = new Thickness(1),
            Child = content,
        };
        return new Grid { Background = new SolidColorBrush(Colors.Black, 0.45), Children = { card } };
    }

    private sealed class Closer(Action close) : IDisposable
    {
        private Action? _close = close;

        public void Dispose()
        {
            _close?.Invoke();
            _close = null;
        }
    }
}
