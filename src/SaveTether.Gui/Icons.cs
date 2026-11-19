using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SaveTether.Gui.Presentation;

namespace SaveTether.Gui;

/// <summary>
/// The tray and window icons, drawn at start so there are no image files to ship. Each state has its own
/// shape as well as its colour (desktop-app-spec.md, section 3): a tick for green, an exclamation mark
/// for amber, so the state does not depend on telling colours apart.
/// </summary>
internal static class Icons
{
    public static readonly Color Green = Color.FromRgb(0x2E, 0x7D, 0x32);
    public static readonly Color Amber = Color.FromRgb(0xC7, 0x7C, 0x02);
    public static readonly Color Grey = Color.FromRgb(0x75, 0x75, 0x75);

    private const int Size = 64;

    private static readonly Lazy<WindowIcon> GreenIcon = new(() => Draw(Green, "M 18 33 L 28 43 L 47 22"));
    private static readonly Lazy<WindowIcon> AmberIcon = new(() => Draw(Amber, "M 32 15 L 32 37 M 32 47 L 32 48"));

    public static WindowIcon For(bool amber) => amber ? AmberIcon.Value : GreenIcon.Value;

    public static Color ColourOf(Shade shade) => shade switch
    {
        Shade.Green => Green,
        Shade.Amber => Amber,
        _ => Grey,
    };

    private static WindowIcon Draw(Color fill, string mark)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            context.DrawEllipse(new SolidColorBrush(fill), null, new Point(Size / 2.0, Size / 2.0), Size / 2.0 - 2, Size / 2.0 - 2);
            var pen = new Pen(Brushes.White, 7, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            context.DrawGeometry(null, pen, StreamGeometry.Parse(mark));
        }

        using var png = new MemoryStream();
        bitmap.Save(png, PngBitmapEncoderOptions.Default);
        png.Position = 0;
        return new WindowIcon(png);
    }
}
