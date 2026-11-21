using Avalonia.Controls;
using Avalonia.Media;

namespace SaveTether.Gui;

/// <summary>The look of a button that removes something for good (the owner asked for red): a red fill with white text.</summary>
internal static class Danger
{
    public static readonly Color Red = Color.FromRgb(0xC0, 0x39, 0x2B);

    public static void Style(Button button)
    {
        ArgumentNullException.ThrowIfNull(button);
        button.Background = new SolidColorBrush(Red);
        button.Foreground = Brushes.White;
        button.FontWeight = FontWeight.SemiBold;
    }
}
