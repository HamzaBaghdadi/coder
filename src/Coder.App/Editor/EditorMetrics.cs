using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace Coder.App.Editor;

/// <summary>
/// Font measurements for the editor. Every line is <see cref="LineHeight"/> tall; <see cref="CharWidth"/>
/// is the width of one monospaced cell (used for indent guides and scrolling, not for glyph positions).
/// </summary>
public sealed class EditorMetrics
{
    private readonly Dictionary<(IBrush Brush, Typeface Typeface), TextRunProperties> _runProperties = [];

    public EditorMetrics(Typeface typeface, double fontSize, double lineHeightFactor)
    {
        Typeface = typeface;
        FontSize = fontSize;

        // Measuring a long run averages out sub-pixel rounding of a single glyph.
        const int sampleLength = 100;
        var sample = new FormattedText(new string('m', sampleLength), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, fontSize, null);
        CharWidth = sample.WidthIncludingTrailingWhitespace / sampleLength;
        LineHeight = Math.Ceiling(fontSize * lineHeightFactor);
        TextTop = Math.Round((LineHeight - sample.Height) / 2);
    }

    public Typeface Typeface { get; }

    public double FontSize { get; }

    /// <summary>
    /// Run properties for text in <paramref name="brush"/>, in <paramref name="font"/> (default: the
    /// coding font). Shared across lines: one instance per color and font.
    /// </summary>
    public TextRunProperties RunProperties(IBrush brush, Typeface font = default)
    {
        var typeface = font == default ? Typeface : font;
        if (!_runProperties.TryGetValue((brush, typeface), out var properties))
        {
            properties = new GenericTextRunProperties(typeface, FontSize, foregroundBrush: brush);
            _runProperties[(brush, typeface)] = properties;
        }

        return properties;
    }

    public double CharWidth { get; }

    public double LineHeight { get; }

    /// <summary>Vertical offset that centers glyphs inside a line.</summary>
    public double TextTop { get; }
}
