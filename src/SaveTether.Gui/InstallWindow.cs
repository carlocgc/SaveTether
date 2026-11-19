using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SaveTether.Gui.Presentation;

namespace SaveTether.Gui;

/// <summary>
/// The install or update offer (desktop-app-spec.md, section 11, "Installing"), in a small window of its own, shown
/// before the main window: a copy started only to update the installed one has no main window to show it in.
/// Closing it is "Not now".
/// </summary>
internal sealed class InstallWindow : Window
{
    private readonly StackPanel _body = new() { Spacing = 14, Margin = new Thickness(24) };
    private TaskCompletionSource<bool>? _answer;

    public InstallWindow()
    {
        Title = Texts.AppName;
        Icon = Icons.For(amber: false);
        Width = 600;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = _body;
        Closing += (_, _) => _answer?.TrySetResult(false);
    }

    /// <summary>Shows the offer and answers whether it was accepted.</summary>
    public Task<bool> AskAsync(InstallOfferView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        _answer = new TaskCompletionSource<bool>();
        var notNow = new Button { Content = "Not now" };
        var confirm = new Button { Content = view.Confirm };
        notNow.Click += (_, _) => _answer.TrySetResult(false);
        confirm.Click += (_, _) => _answer.TrySetResult(true);
        Fill(view.Title, view.Text, null, Row(notNow, confirm));
        Show();
        Activate();
        notNow.Focus();
        return _answer.Task;
    }

    /// <summary>A sentence with OK, in place of the offer: what happened, when it is not what was hoped.</summary>
    public Task InformAsync(string title, string text)
    {
        var done = new TaskCompletionSource();
        var ok = new Button { Content = "OK" };
        ok.Click += (_, _) => done.TrySetResult();
        Closing += (_, _) => done.TrySetResult();
        Fill(title, text, null, Row(ok));
        Show();
        ok.Focus();
        return done.Task;
    }

    /// <summary>"Installing…" while the files are copied.</summary>
    public void Working(string text) => Fill(text, "This takes a few seconds.", null, null);

    private void Fill(string title, string text, Control? extra, Control? buttons)
    {
        _body.Children.Clear();
        var heading = new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(heading, title);
        _body.Children.Add(heading);
        _body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        if (extra is not null)
        {
            _body.Children.Add(extra);
        }

        if (buttons is not null)
        {
            _body.Children.Add(buttons);
        }
    }

    private static StackPanel Row(params Button[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var button in buttons)
        {
            row.Children.Add(button);
        }

        return row;
    }
}
