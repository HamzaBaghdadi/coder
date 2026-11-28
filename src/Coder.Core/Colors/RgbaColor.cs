namespace Coder.Core.Colors;

/// <summary>An sRGB color with 8-bit channels and alpha (255 is opaque).</summary>
public readonly record struct RgbaColor(byte R, byte G, byte B, byte A = 255)
{
    /// <summary>Alpha as 0..1.</summary>
    public double Opacity => A / 255.0;

    public HsvColor ToHsv()
    {
        var (r, g, b) = (R / 255.0, G / 255.0, B / 255.0);
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        var hue = Hue(r, g, b, max, delta);
        return new HsvColor(hue, max == 0 ? 0 : delta / max, max, Opacity);
    }

    public HslColor ToHsl()
    {
        var (r, g, b) = (R / 255.0, G / 255.0, B / 255.0);
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        var lightness = (max + min) / 2;
        var saturation = delta == 0 ? 0 : delta / (1 - Math.Abs(2 * lightness - 1));
        return new HslColor(Hue(r, g, b, max, delta), Math.Clamp(saturation, 0, 1), lightness, Opacity);
    }

    internal static byte ToByte(double unit) => (byte)Math.Clamp(Math.Round(unit * 255), 0, 255);

    private static double Hue(double r, double g, double b, double max, double delta)
    {
        if (delta == 0)
        {
            return 0;
        }

        var hue = max == r ? (g - b) / delta % 6
            : max == g ? (b - r) / delta + 2
            : (r - g) / delta + 4;
        hue *= 60;
        return hue < 0 ? hue + 360 : hue;
    }
}

/// <summary>Hue (0..360), saturation, value and alpha (0..1): the color picker's own model.</summary>
public readonly record struct HsvColor(double H, double S, double V, double A = 1)
{
    public RgbaColor ToRgba()
    {
        var hue = ((H % 360) + 360) % 360 / 60;
        var chroma = V * S;
        var x = chroma * (1 - Math.Abs(hue % 2 - 1));
        var (r, g, b) = (int)hue switch
        {
            0 => (chroma, x, 0.0),
            1 => (x, chroma, 0.0),
            2 => (0.0, chroma, x),
            3 => (0.0, x, chroma),
            4 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x),
        };
        var m = V - chroma;
        return new RgbaColor(RgbaColor.ToByte(r + m), RgbaColor.ToByte(g + m), RgbaColor.ToByte(b + m), RgbaColor.ToByte(A));
    }
}

/// <summary>Hue (0..360), saturation, lightness and alpha (0..1), as CSS hsl() writes them.</summary>
public readonly record struct HslColor(double H, double S, double L, double A = 1)
{
    public RgbaColor ToRgba()
    {
        var value = L + S * Math.Min(L, 1 - L);
        var saturation = value == 0 ? 0 : 2 * (1 - L / value);
        return new HsvColor(H, saturation, value, A).ToRgba();
    }
}
