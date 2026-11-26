using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

// Turns the logo artwork into the app's icon files.
// Usage: dotnet run --project tools/Coder.IconTool -- <source-image> <assets-folder>
//
// The artwork is a rounded tile on a solid black canvas. The tool finds the tile, measures its corner
// radius, makes everything outside it transparent, and writes coder.png (in-app logo) and coder.ico
// (Windows executable icon, several sizes).
if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: Coder.IconTool <source-image> <assets-folder>");
    return 1;
}

var source = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);

using var session = HeadlessUnitTestSession.StartNew(typeof(IconToolApp));
await session.Dispatch(() =>
{
    using var artwork = new Bitmap(source);
    var tile = TileDetector.Find(artwork);
    Console.WriteLine($"Tile: {tile.Bounds}, corner radius {tile.CornerRadius:F1}px");

    File.WriteAllBytes(Path.Combine(output, "coder.png"), Png(Render(artwork, tile, 512)));
    int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
    File.WriteAllBytes(Path.Combine(output, "coder.ico"), IcoWriter.Build(sizes.Select(size => (size, Png(Render(artwork, tile, size)))).ToList()));
    Console.WriteLine($"Wrote coder.png and coder.ico to {output}");
}, CancellationToken.None);
return 0;

static RenderTargetBitmap Render(Bitmap artwork, Tile tile, int size)
{
    var bitmap = new RenderTargetBitmap(new PixelSize(size, size));
    using var context = bitmap.CreateDrawingContext();
    var scale = size / tile.Bounds.Width;
    var target = new Rect(0, 0, size, size);
    using (context.PushClip(new RoundedRect(target, tile.CornerRadius * scale)))
    {
        context.DrawImage(artwork, tile.Bounds, target);
    }

    return bitmap;
}

static byte[] Png(RenderTargetBitmap bitmap)
{
    using (bitmap)
    using (var stream = new MemoryStream())
    {
        bitmap.Save(stream);
        return stream.ToArray();
    }
}

internal readonly record struct Tile(Rect Bounds, double CornerRadius);

/// <summary>Finds the rounded tile inside the black canvas.</summary>
internal static class TileDetector
{
    private const int BlackThreshold = 28;

    public static Tile Find(Bitmap artwork)
    {
        var (pixels, width, height) = ReadPixels(artwork);
        bool IsTile(int x, int y)
        {
            var i = (y * width + x) * 4;
            return Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2])) > BlackThreshold;
        }

        int left = width, top = height, right = 0, bottom = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (IsTile(x, y))
                {
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
            }
        }

        // Walk the diagonal from the bounding box corner; for a corner of radius r the tile edge is
        // reached after r * (sqrt(2) - 1) pixels along each axis.
        var step = 0;
        while (!IsTile(left + step, top + step))
        {
            step++;
        }

        var radius = step * Math.Sqrt(2) / (Math.Sqrt(2) - 1);
        var side = Math.Max(right - left, bottom - top) + 1;
        return new Tile(new Rect(left, top, side, side), radius);
    }

    private static (byte[] Pixels, int Width, int Height) ReadPixels(Bitmap artwork)
    {
        var size = artwork.PixelSize;
        using var copy = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var buffer = copy.Lock();
        artwork.CopyPixels(buffer);

        var pixels = new byte[size.Width * size.Height * 4];
        for (var y = 0; y < size.Height; y++)
        {
            Marshal.Copy(buffer.Address + y * buffer.RowBytes, pixels, y * size.Width * 4, size.Width * 4);
        }

        return (pixels, size.Width, size.Height);
    }
}

/// <summary>Writes an ICO container with embedded PNG images (supported since Windows Vista).</summary>
internal static class IcoWriter
{
    public static byte[] Build(IReadOnlyList<(int Size, byte[] Png)> images)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((short)0);
        writer.Write((short)1);
        writer.Write((short)images.Count);

        var offset = 6 + 16 * images.Count;
        foreach (var (size, png) in images)
        {
            writer.Write((byte)(size >= 256 ? 0 : size)); // 0 means 256
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((short)1);
            writer.Write((short)32);
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
}

internal sealed class IconToolApp : Application
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<IconToolApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
