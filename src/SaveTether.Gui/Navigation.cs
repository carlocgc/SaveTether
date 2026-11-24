using System.Globalization;
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

    /// <summary>The list's column names, pinned at the top of the scrolling area; null for a page without columns.</summary>
    ContentControl? Columns => null;
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

    /// <summary>The window's font size (<see cref="MainWindow"/>), which the rows' text inherits.</summary>
    public const double BodySize = 15;

    /// <summary>
    /// A list column's width: its widest text or its name in <see cref="ColumnHeader"/>, whichever is wider, plus a 24 px gap, so
    /// the column fits its content in whatever font the platform has. At most <paramref name="max"/>, beyond which the text is
    /// trimmed: the last column and the buttons need the rest of the row.
    /// </summary>
    public static double FitWidth(string label, IEnumerable<string> texts, double max, double fontSize = BodySize, FontWeight weight = FontWeight.Normal)
    {
        var widest = texts.Select(t => TextWidth(t, fontSize, weight)).Append(TextWidth(label, HeaderSize, FontWeight.SemiBold)).Max();
        return Math.Min(max, Math.Ceiling(widest) + 24);
    }

    /// <summary>Column widths as a grid's column definitions take them ("120,150,*").</summary>
    public static string Widths(params object[] widths) => string.Join(",", widths.Select(w => Convert.ToString(w, CultureInfo.InvariantCulture)));

    private const double HeaderSize = 13;

    private static double TextWidth(string text, double fontSize, FontWeight weight) =>
        new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, weight), fontSize, null).Width;

    /// <summary>
    /// A list's column names, for <see cref="IScreen.Columns"/>: a grid with the rows' column widths, so each name sits over
    /// its column. An empty label leaves its column unnamed.
    /// </summary>
    public static Border ColumnHeader(string columns, IReadOnlyList<string> labels)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(columns) };
        for (var i = 0; i < labels.Count; i++)
        {
            var label = new TextBlock { Text = labels[i], FontSize = HeaderSize, FontWeight = FontWeight.SemiBold, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(label, i);
            grid.Children.Add(label);
        }

        return new Border
        {
            Child = grid,
            Margin = new Thickness(0, 0, 0, 4),
            Padding = new Thickness(0, 2, 0, 4),
            BorderBrush = new SolidColorBrush(Color.FromArgb(90, 128, 128, 128)),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }

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
