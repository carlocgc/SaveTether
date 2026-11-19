using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SaveTether.Gui.Presentation;

namespace SaveTether.Gui;

/// <summary>
/// The app's icon, "linked floppy" (build/render-icon.cs draws it; the files are in Assets), and the tray's. The
/// window, its title bar and the taskbar show the app's icon, which says which app it is. The tray shows the same icon
/// with a badge for the state (desktop-app-spec.md, section 3): a tick on green, an exclamation mark on amber, so the
/// state does not depend on telling colours apart.
/// </summary>
internal static class Icons
{
    public static readonly Color Green = Color.FromRgb(0x2E, 0x7D, 0x32);
    public static readonly Color Amber = Color.FromRgb(0xC7, 0x7C, 0x02);
    public static readonly Color Grey = Color.FromRgb(0x75, 0x75, 0x75);

    private const int TraySize = 32;

    private static readonly Lazy<WindowIcon> AppIcon = new(() =>
        // Windows picks the size it needs from the .ico; elsewhere the large PNG is scaled.
        new WindowIcon(Resource(OperatingSystem.IsWindows() ? "savetether.ico" : "savetether.png")));

    private static readonly Lazy<WindowIcon> GreenTray = new(() => Tray(Green, "M 18 25 L 21.5 28.5 L 28 21"));
    private static readonly Lazy<WindowIcon> AmberTray = new(() => Tray(Amber, "M 23 19 L 23 25 M 23 29.2 L 23 29.4"));

    /// <summary>The app's icon, for its windows: the title bar and the taskbar.</summary>
    public static WindowIcon App => AppIcon.Value;

    /// <summary>The tray icon for the state: the app's icon with a green or amber badge.</summary>
    public static WindowIcon For(bool amber) => amber ? AmberTray.Value : GreenTray.Value;

    public static Color ColourOf(Shade shade) => shade switch
    {
        Shade.Green => Green,
        Shade.Amber => Amber,
        _ => Grey,
    };

    private static Stream Resource(string name) =>
        typeof(Icons).Assembly.GetManifestResourceStream(name)
        ?? throw new InvalidOperationException($"The resource '{name}' is not built into the app.");

    /// <summary>The tray base (the simpler drawing, 32 pixels) with a badge in the lower right corner.</summary>
    private static WindowIcon Tray(Color fill, string mark)
    {
        using var source = Resource("savetether-tray.png");
        using var icon = new Bitmap(source);
        using var bitmap = new RenderTargetBitmap(new PixelSize(TraySize, TraySize), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            context.DrawImage(icon, new Rect(0, 0, TraySize, TraySize));
            context.DrawEllipse(new SolidColorBrush(fill), new Pen(Brushes.White, 1.5), new Point(23, 24), 8, 8);
            var pen = new Pen(Brushes.White, 2.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            context.DrawGeometry(null, pen, StreamGeometry.Parse(mark));
        }

        using var png = new MemoryStream();
        bitmap.Save(png, PngBitmapEncoderOptions.Default);
        png.Position = 0;
        return new WindowIcon(png);
    }
}
