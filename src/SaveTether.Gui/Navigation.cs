using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace SaveTether.Gui;

/// <summary>
/// The way back from a page shown in place of the Apps page (Add app, Backups, Settings): a button at the top,
/// where the Apps page has its own buttons, so it is in the same place on every page however long the page is
/// (asked for by the owner). A page's own action, such as Add, stays below what it acts on.
/// </summary>
internal static class Navigation
{
    public static Control BackRow(string label, Action back)
    {
        ArgumentNullException.ThrowIfNull(back);
        var button = new Button { Content = label };
        button.Click += (_, _) => back();
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 6),
            Children = { button },
        };
    }
}
