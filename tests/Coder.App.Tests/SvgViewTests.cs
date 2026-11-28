using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Coder.App.Controls;
using Coder.App.Theme;
using Coder.Core.Svg;

namespace Coder.App.Tests;

public sealed class SvgViewTests
{
    /// <summary>A small logo exercising gradients, transforms, a style sheet, dashes, group opacity and text.</summary>
    private const string Logo = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="240" height="170" viewBox="0 0 240 170">
          <defs>
            <linearGradient id="bg" x1="0" y1="0" x2="1" y2="1">
              <stop offset="0" stop-color="#0ea5e9"/>
              <stop offset="1" stop-color="#6366f1"/>
            </linearGradient>
            <radialGradient id="glow" cx="0.3" cy="0.3" r="0.7">
              <stop offset="0" stop-color="#ffffff" stop-opacity="0.85"/>
              <stop offset="1" stop-color="#ffffff" stop-opacity="0"/>
            </radialGradient>
            <linearGradient id="bar" gradientUnits="userSpaceOnUse" x1="160" y1="0" x2="230" y2="0" gradientTransform="rotate(0)">
              <stop offset="0" stop-color="#f59e0b"/>
              <stop offset="1" stop-color="#ef4444"/>
            </linearGradient>
            <style>
              .mark { fill: none; stroke: #ffffff; stroke-width: 9; stroke-linecap: round; stroke-linejoin: round }
            </style>
            <circle id="dot" r="6"/>
          </defs>
          <rect x="10" y="10" width="140" height="140" rx="30" fill="url(#bg)"/>
          <circle cx="80" cy="80" r="66" fill="url(#glow)"/>
          <g transform="translate(80 80) rotate(-10)">
            <path class="mark" d="M-28-24 L-50 0 L-28 24 M28-24 L50 0 L28 24"/>
            <line class="mark" x1="12" y1="-34" x2="-12" y2="34" style="stroke: #fde68a; stroke-width: 6" stroke-dasharray="6 10"/>
          </g>
          <g opacity="0.85">
            <polygon points="165,25 228,25 196,82" fill="#f43f5e" stroke="#881337" stroke-width="3" stroke-linejoin="round"/>
          </g>
          <rect x="160" y="96" width="70" height="14" rx="7" fill="url(#bar)"/>
          <g fill="#22c55e">
            <use href="#dot" x="172" y="128"/>
            <use xlink:href="#dot" x="195" y="128" fill="#a3e635"/>
            <use href="#dot" x="218" y="128" transform="rotate(15 218 128)" style="fill: hsl(190, 90%, 55%)"/>
          </g>
          <path d="M 0 0 L oops" fill="red"/>
          <text x="80" y="166" text-anchor="middle" font-family="sans-serif" font-size="15" font-weight="bold" fill="#e2e8f0">Coder <tspan fill="#38bdf8">SVG</tspan></text>
        </svg>
        """;

    [AvaloniaFact]
    public void Renders_a_logo_with_gradients_transforms_strokes_and_text()
    {
        var document = SvgDocument.Parse(Logo);
        var view = new SvgView { Document = document, ShowTransparencyGrid = true };
        var window = new Window { Width = 720, Height = 510, Background = Palette.EditorBackground, Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, view.SkippedItemCount);

            // Fit: 240x170 scaled by min(720/240, 510/170) = 3, filling the window.
            var image = view.ImageRect;
            Assert.Equal(3, image.Width / document.Width, 3);

            using var frame = window.CaptureRenderedFrame()!;
            SaveScreenshot(frame, "svg-view");
            var pixels = Pixels(frame);

            // The gradient square: top-left is sky blue, bottom-right indigo.
            var topLeft = pixels(image.X + (24 * 3), image.Y + (40 * 3));
            var bottomRight = pixels(image.X + (140 * 3), image.Y + (140 * 3));
            Assert.True(topLeft.B > 180 && topLeft.G > 120, $"top-left {topLeft}");
            Assert.True(bottomRight.R > 70 && bottomRight.G < 140 && bottomRight.B > 200, $"bottom-right {bottomRight}");

            // The translucent triangle is red, over the grid.
            var triangle = pixels(image.X + (196 * 3), image.Y + (40 * 3));
            Assert.True(triangle.R > 180 && triangle.G < 110, $"triangle {triangle}");

            // The user-space gradient bar runs from amber (left) to red (right).
            var barLeft = pixels(image.X + (166 * 3), image.Y + (103 * 3));
            var barRight = pixels(image.X + (224 * 3), image.Y + (103 * 3));
            Assert.True(barLeft.G > barRight.G + 40, $"bar {barLeft} → {barRight}");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Zoom_caps_the_scale_and_centers_the_image()
    {
        var view = new SvgView { Document = SvgDocument.Parse(Logo), Zoom = 1 };
        var window = new Window { Width = 600, Height = 400, Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new Rect(180, 115, 240, 170), view.ImageRect);
            Assert.Equal(0.5, SvgView.ScaleFor(200, 100, 100, 400, 0));
            Assert.Equal(2, SvgView.ScaleFor(10, 10, 100, 20, 0));
            Assert.Equal(1, SvgView.ScaleFor(10, 10, 100, 20, 1));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void An_unreadable_element_is_skipped_and_the_rest_still_draws()
    {
        var document = SvgDocument.Parse("""
            <svg xmlns="http://www.w3.org/2000/svg" width="20" height="20">
              <path d="garbage"/>
              <rect width="20" height="20" fill="#00ff00"/>
            </svg>
            """);
        var view = new SvgView { Document = document };
        var window = new Window { Width = 100, Height = 100, Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            using var frame = window.CaptureRenderedFrame()!;
            var center = Pixels(frame)(50, 50);
            Assert.Equal(1, view.SkippedItemCount);
            Assert.True(center.G > 200 && center.R < 50, $"center {center}");
        }
        finally
        {
            window.Close();
        }
    }

    private readonly record struct Rgb(byte R, byte G, byte B);

    /// <summary>Reads the frame once; the returned function samples a pixel in logical coordinates.</summary>
    private static Func<double, double, Rgb> Pixels(WriteableBitmap frame)
    {
        var (width, height) = (frame.PixelSize.Width, frame.PixelSize.Height);
        var stride = width * 4;
        var buffer = Marshal.AllocHGlobal(stride * height);
        var pixels = new byte[stride * height];
        try
        {
            frame.CopyPixels(new PixelRect(0, 0, width, height), buffer, pixels.Length, stride);
            Marshal.Copy(buffer, pixels, 0, pixels.Length);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        var bgra = frame.Format == Avalonia.Platform.PixelFormat.Bgra8888;
        var scale = width / frame.Size.Width;
        return (x, y) =>
        {
            var i = ((int)(y * scale) * stride) + ((int)(x * scale) * 4);
            return bgra ? new Rgb(pixels[i + 2], pixels[i + 1], pixels[i]) : new Rgb(pixels[i], pixels[i + 1], pixels[i + 2]);
        };
    }

    /// <summary>Saves to artifacts/screenshots, finding the repository from this source file (builds run elsewhere).</summary>
    private static void SaveScreenshot(Bitmap frame, string name, [System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "")
    {
        var root = Path.GetDirectoryName(sourceFile)!;
        while (!File.Exists(Path.Combine(root, "Coder.sln")))
        {
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Coder.sln not found.");
        }

        var directory = Path.Combine(root, "artifacts", "screenshots");
        Directory.CreateDirectory(directory);
        frame.Save(Path.Combine(directory, $"{name}.png"));
    }
}
