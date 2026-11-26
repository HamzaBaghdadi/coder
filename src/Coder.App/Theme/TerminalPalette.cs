using Avalonia.Media;
using Avalonia.Media.Immutable;
using Coder.Core.Terminal;

namespace Coder.App.Theme;

/// <summary>
/// Terminal colors. The 16 ANSI colors come from the current theme (readable on its background, without
/// neon glare on the dark one). The rest of the 256-color palette follows the xterm standard.
/// </summary>
public static class TerminalPalette
{
    public static readonly IBrush Foreground = Palette.TextPrimary;
    public static readonly IBrush Background = Palette.EditorBackground;

    private static readonly IBrush[] Indexed = Enumerable.Range(0, 256).Select(CreateIndexed).ToArray();
    private static readonly Dictionary<int, IBrush> RgbBrushes = [];

    /// <summary>The brush for a cell color; <paramref name="fallback"/> is used for the default color.</summary>
    public static IBrush Resolve(TerminalColor color, IBrush fallback) => color.Kind switch
    {
        TerminalColorKind.Indexed => Indexed[color.Value],
        TerminalColorKind.Rgb => RgbBrush(color.Value),
        _ => fallback,
    };

    private static IBrush RgbBrush(int rgb)
    {
        if (!RgbBrushes.TryGetValue(rgb, out var brush))
        {
            brush = new ImmutableSolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            if (RgbBrushes.Count < 4096)
            {
                RgbBrushes[rgb] = brush;
            }
        }

        return brush;
    }

    private static IBrush CreateIndexed(int index)
    {
        if (index < 16)
        {
            return Palette.Token("Ansi" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)); // follows the theme
        }

        if (index < 232)
        {
            // 6×6×6 color cube.
            var i = index - 16;
            static byte Level(int v) => (byte)(v == 0 ? 0 : 55 + v * 40);
            return new ImmutableSolidColorBrush(Color.FromRgb(Level(i / 36), Level(i / 6 % 6), Level(i % 6)));
        }

        // 24 grays.
        var gray = (byte)(8 + (index - 232) * 10);
        return new ImmutableSolidColorBrush(Color.FromRgb(gray, gray, gray));
    }
}
