#:package SkiaSharp@3.119.4
#:property PublishAot=false

// SaveTether's app icon, "linked floppy" (chosen by the owner on 2026-11-19): a save disk with two linked rings.
// The shapes below are the only source. This writes them as an SVG, PNGs at each size, and a Windows .ico:
//
//   dotnet run build/render-icon.cs -- src/SaveTether.Gui/Assets
//
// Sizes of 24 pixels and under use a simpler drawing (no shutter window, heavier rings), so the link still reads in
// a title bar or tray. The outputs are committed; run this again after changing a shape.

using System.Text;
using SkiaSharp;

var outDir = args.Length > 0 ? args[0] : ".";
Directory.CreateDirectory(outDir);

const string Blue = "#185FA5";
const string LightBlue = "#378ADD";
const string White = "#FFFFFF";

// On a 64 by 64 grid. Each shape: SVG path data, fill, and stroke width (0 for a filled shape).
(string Path, string Colour, float Stroke)[] Large =
[
    ("M16 2 H48 A14 14 0 0 1 62 16 V48 A14 14 0 0 1 48 62 H16 A14 14 0 0 1 2 48 V16 A14 14 0 0 1 16 2 Z", Blue, 0),
    ("M16 12 H42 L50 20 V48 A3 3 0 0 1 47 51 H17 A3 3 0 0 1 14 48 V14 A2 2 0 0 1 16 12 Z", White, 0),
    ("M21 12 H38 V22 A1.5 1.5 0 0 1 36.5 23.5 H22.5 A1.5 1.5 0 0 1 21 22 Z", Blue, 0),
    ("M31 14.5 H35 V21 H31 Z", White, 0),
    ("M33 38 A6 6 0 1 1 21 38 A6 6 0 1 1 33 38 Z", Blue, 4),
    ("M43 38 A6 6 0 1 1 31 38 A6 6 0 1 1 43 38 Z", LightBlue, 4),
    // The left ring again over the right one at the top, so the two read as linked rather than stacked.
    ("M27.5 32.0 A6 6 0 0 1 32.9 37.0", Blue, 4),
];

(string Path, string Colour, float Stroke)[] Small =
[
    ("M14 2 H50 A12 12 0 0 1 62 14 V50 A12 12 0 0 1 50 62 H14 A12 12 0 0 1 2 50 V14 A12 12 0 0 1 14 2 Z", Blue, 0),
    ("M14 10 H43 L52 19 V50 A3 3 0 0 1 49 53 H15 A3 3 0 0 1 12 50 V12 A2 2 0 0 1 14 10 Z", White, 0),
    ("M20 10 H40 V23 H20 Z", Blue, 0),
    ("M34 39 A7 7 0 1 1 20 39 A7 7 0 1 1 34 39 Z", Blue, 6),
    ("M45 39 A7 7 0 1 1 31 39 A7 7 0 1 1 45 39 Z", LightBlue, 6),
    ("M27.0 32.0 A7 7 0 0 1 33.9 38.0", Blue, 6),
];

int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
var pngs = sizes.Select(size => (Size: size, Png: Render(size <= 24 ? Small : Large, size))).ToList();

// savetether.ico: the Windows program and installer icon, every size. savetether.png: the Linux app menu and window
// icon. savetether-tray.png: the simpler drawing, which the app draws its green or amber badge on for the tray.
File.WriteAllBytes(Path.Combine(outDir, "savetether.ico"), Ico(pngs));
File.WriteAllBytes(Path.Combine(outDir, "savetether.png"), pngs.Single(p => p.Size == 256).Png);
File.WriteAllBytes(Path.Combine(outDir, "savetether-tray.png"), Render(Small, 32));
File.WriteAllText(Path.Combine(outDir, "savetether.svg"), Svg(Large), new UTF8Encoding(false));
Console.WriteLine($"wrote savetether.ico ({string.Join(", ", sizes)}), savetether.png, savetether-tray.png and savetether.svg to {Path.GetFullPath(outDir)}");

static byte[] Render((string Path, string Colour, float Stroke)[] shapes, int size)
{
    using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
    var canvas = surface.Canvas;
    canvas.Clear(SKColors.Transparent);
    canvas.Scale(size / 64f);
    foreach (var (data, colour, stroke) in shapes)
    {
        using var path = SKPath.ParseSvgPathData(data);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Color = SKColor.Parse(colour),
            Style = stroke > 0 ? SKPaintStyle.Stroke : SKPaintStyle.Fill,
            StrokeWidth = stroke,
            StrokeCap = SKStrokeCap.Round,
        };
        canvas.DrawPath(path, paint);
    }

    using var image = surface.Snapshot();
    using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
    return encoded.ToArray();
}

// An .ico holding a PNG for each size, which Windows reads at every size since Vista.
static byte[] Ico(List<(int Size, byte[] Png)> images)
{
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);
    writer.Write((ushort)0);
    writer.Write((ushort)1);
    writer.Write((ushort)images.Count);
    var offset = 6 + (16 * images.Count);
    foreach (var (size, png) in images)
    {
        writer.Write((byte)(size >= 256 ? 0 : size));
        writer.Write((byte)(size >= 256 ? 0 : size));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(png.Length);
        writer.Write(offset);
        offset += png.Length;
    }

    foreach (var (_, png) in images)
    {
        writer.Write(png);
    }

    return stream.ToArray();
}

static string Svg((string Path, string Colour, float Stroke)[] shapes)
{
    var svg = new StringBuilder("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 64 64\" width=\"256\" height=\"256\">\n");
    foreach (var (data, colour, stroke) in shapes)
    {
        svg.Append(stroke > 0
            ? $"  <path d=\"{data}\" fill=\"none\" stroke=\"{colour}\" stroke-width=\"{stroke}\" stroke-linecap=\"round\"/>\n"
            : $"  <path d=\"{data}\" fill=\"{colour}\"/>\n");
    }

    return svg.Append("</svg>\n").ToString();
}
