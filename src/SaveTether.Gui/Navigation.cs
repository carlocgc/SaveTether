using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace SaveTether.Gui;

/// <summary>
/// A page shown in place of the Apps page (Add app, Backups, Settings). Its <see cref="Bar"/> is shown above the
/// scrolling page, where the Apps page has its own buttons, so the way back stays in one place and in view however
/// long the page is (asked for by the owner).
/// </summary>
internal interface IScreen
{
    /// <summary>The page's buttons above the scrolling area: the way back. A page may change its content.</summary>
    ContentControl Bar { get; }
}

internal static class Navigation
{
    /// <summary>
    /// An app's name above its rows (Apps and Backups): a tinted band with a line under it, so one app's rows
    /// read apart from the next. The tint is a grey at low opacity, so it suits the light and the dark theme.
    /// </summary>
    public static Control GroupHeader(string text) => new Border
    {
        Margin = new Thickness(0, 12, 0, 4),
        Padding = new Thickness(10, 5),
        CornerRadius = new CornerRadius(4),
        Background = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(140, 128, 128, 128)),
        BorderThickness = new Thickness(0, 0, 0, 2),
        Child = new TextBlock { Text = text, FontSize = 17, FontWeight = FontWeight.SemiBold },
    };

    /// <summary>The way back from a page, as a row for a screen's <see cref="IScreen.Bar"/>.</summary>
    public static Control BackRow(string label, Action back)
    {
        ArgumentNullException.ThrowIfNull(back);
        var button = new Button { Content = label };
        button.Click += (_, _) => back();
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            Children = { button },
        };
    }
}
