using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace SaveTether.Gui;

/// <summary>
/// Dialogs inside the main window (desktop-app-spec.md, principles 5 and 6): the page is dimmed and cannot
/// take focus, the dialog opens with focus on the choice that changes nothing, and Escape is Cancel.
/// </summary>
internal sealed class Overlay : Panel
{
    private readonly Control _page;

    public Overlay(Control page)
    {
        _page = page;
        Children.Add(page);
    }

    /// <summary>Tells something that needs no choice; closed with OK or Escape.</summary>
    public Task InformAsync(string title, string text) => Show(title, text, confirmLabel: null);

    /// <summary>Asks, and answers true only for the confirming button.</summary>
    public Task<bool> ConfirmAsync(string title, string text, string confirmLabel) => Show(title, text, confirmLabel);

    private Task<bool> Show(string title, string text, string? confirmLabel)
    {
        var answer = new TaskCompletionSource<bool>();
        var cancel = new Button { Content = confirmLabel is null ? "OK" : "Cancel" };
        var confirm = new Button { Content = confirmLabel, IsVisible = confirmLabel is not null };
        var card = new Border
        {
            MaxWidth = 560,
            Padding = new Thickness(24),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark
                ? new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B))
                : Brushes.White,
            BorderBrush = new SolidColorBrush(Icons.Amber),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 16,
                Children =
                {
                    new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, confirm } },
                },
            },
        };
        var layer = new Grid { Background = new SolidColorBrush(Colors.Black, 0.45), Children = { card } };

        void Close(bool result)
        {
            Children.Remove(layer);
            _page.IsEnabled = true;
            answer.TrySetResult(result);
        }

        cancel.Click += (_, _) => Close(false);
        confirm.Click += (_, _) => Close(true);
        layer.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close(false);
            }
        };

        _page.IsEnabled = false;
        Children.Add(layer);
        cancel.AttachedToVisualTree += (_, _) => cancel.Focus(NavigationMethod.Tab);
        return answer.Task;
    }
}
