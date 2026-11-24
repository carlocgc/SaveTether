using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

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
        // Fluent's hover and pressed states take these resources, not Background: without them the button turned grey under
        // the pointer. Disabled keeps the theme's grey, so a disabled button still looks disabled.
        button.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Color.FromRgb(0xD6, 0x4A, 0x3B));
        button.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Color.FromRgb(0xA5, 0x30, 0x24));
        button.Resources["ButtonForegroundPointerOver"] = Brushes.White;
        button.Resources["ButtonForegroundPressed"] = Brushes.White;
    }

    /// <summary>
    /// Red for text on a dialog, such as the typed phrase's hint. <see cref="Red"/> is too dark to read on the dark dialog
    /// (#2B2B2B), so the dark theme uses a lighter red; both are above 4.5:1 against their dialog.
    /// </summary>
    public static IBrush Text => new SolidColorBrush(
        Application.Current?.ActualThemeVariant == ThemeVariant.Dark ? Color.FromRgb(0xF1, 0x94, 0x8A) : Red);
}
