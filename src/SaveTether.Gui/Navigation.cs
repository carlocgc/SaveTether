using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

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
