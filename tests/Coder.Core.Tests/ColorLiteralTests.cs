using Coder.Core.Colors;

namespace Coder.Core.Tests;

public sealed class ColorLiteralTests
{
    private static readonly ColorSyntax Css = ColorLiterals.SyntaxFor("css");
    private static readonly ColorSyntax CSharp = ColorLiterals.SyntaxFor("csharp");

    private static List<ColorLiteral> Find(string line, ColorSyntax syntax = default)
    {
        var results = new List<ColorLiteral>();
        ColorLiterals.Find(line, syntax, results);
        return results;
    }

    [Theory]
    [InlineData("#f80", 255, 136, 0, 255)]
    [InlineData("#f808", 255, 136, 0, 136)]
    [InlineData("#FF8800", 255, 136, 0, 255)]
    [InlineData("#ff880080", 255, 136, 0, 128)]
    [InlineData("rgb(255, 136, 0)", 255, 136, 0, 255)]
    [InlineData("rgba(255,136,0,0.5)", 255, 136, 0, 128)]
    [InlineData("rgb(255 136 0 / 50%)", 255, 136, 0, 128)]
    [InlineData("rgb(100% 0% 50%)", 255, 0, 128, 255)]
    [InlineData("RGB(1, 2, 3)", 1, 2, 3, 255)]
    [InlineData("hsl(120, 100%, 50%)", 0, 255, 0, 255)]
    [InlineData("hsla(240, 100%, 50%, .25)", 0, 0, 255, 64)]
    [InlineData("hsl(0.5turn 100% 25%)", 0, 128, 128, 255)]
    [InlineData("hsl(30deg 100% 50% / 1)", 255, 128, 0, 255)]
    public void Parses_every_notation(string text, int r, int g, int b, int a)
    {
        var literal = Assert.Single(Find($"  color: {text};"));

        Assert.Equal(9, literal.Start);
        Assert.Equal(text.Length, literal.Length);
        Assert.Equal(new RgbaColor((byte)r, (byte)g, (byte)b, (byte)a), literal.Color);
    }

    [Theory]
    [InlineData("#region Colors")]
    [InlineData("#main { display: block }")]
    [InlineData("commit 1a2b3c4d5e6f7a8b9c0d")]
    [InlineData("see #1a2b3c4 for the hash")]
    [InlineData("a#fff and &#123; and #fff0x")]
    [InlineData("#endif and #define X")]
    [InlineData("rgb(a, b, c) and rgb(1, 2) and rgbx(1, 2, 3) and torgb(1, 2, 3)")]
    [InlineData("hsl(1, 2%, 3%, 4%, 5%)")]
    [InlineData("#abcde and #abcdefa and #fffffffff")]
    public void Skips_things_that_are_not_colors(string line) => Assert.Empty(Find(line, Css));

    [Fact]
    public void Css_id_selectors_are_not_colors_but_values_are()
    {
        Assert.Empty(Find("#bad, #fed {", Css));
        Assert.Single(Find(".a { color: #bad; }", Css));
        Assert.Single(Find("#bad {", CSharp)); // only CSS-like languages have selectors
    }

    [Fact]
    public void Markdown_skips_issue_numbers()
    {
        var markdown = ColorLiterals.SyntaxFor("markdown");

        Assert.Empty(Find("Fixes #123 and #4567", markdown));
        Assert.Single(Find("The accent is #0af", markdown));
    }

    [Fact]
    public void Finds_several_literals_in_order()
    {
        var found = Find("background: linear-gradient(#fff, rgb(0 0 0 / 0.5), hsl(10, 50%, 50%));", Css);

        Assert.Equal(["#fff", "rgb(0 0 0 / 0.5)", "hsl(10, 50%, 50%)"], found.Select(l => "background: linear-gradient(#fff, rgb(0 0 0 / 0.5), hsl(10, 50%, 50%));".Substring(l.Start, l.Length)));
    }

    [Fact]
    public void CSharp_and_xaml_put_alpha_first()
    {
        var literal = Assert.Single(Find("var c = Color.Parse(\"#80FF8800\");", CSharp));

        Assert.Equal(new RgbaColor(255, 136, 0, 128), literal.Color);
        Assert.Equal("#40FF8800", ColorLiterals.Format(literal.Color with { A = 64 }, literal.Style));
        Assert.Equal(new RgbaColor(0, 0, 0, 0), Assert.Single(Find("\"#0000\"", CSharp)).Color);
    }

    [Theory]
    [InlineData("#FF8800", 0, 0, 255, 255, "#0000FF")]
    [InlineData("#f80", 0, 0, 255, 255, "#00f")]
    [InlineData("#f80", 1, 2, 3, 255, "#010203")]
    [InlineData("#f80", 0, 0, 255, 136, "#00f8")]
    [InlineData("#ff8800", 0, 0, 255, 128, "#0000ff80")]
    [InlineData("#ff880080", 0, 0, 255, 255, "#0000ffff")]
    [InlineData("rgb(255, 136, 0)", 1, 2, 3, 255, "rgb(1, 2, 3)")]
    [InlineData("rgb(255,136,0)", 1, 2, 3, 128, "rgba(1,2,3,0.5)")]
    [InlineData("rgba(255, 136, 0, 0.5)", 1, 2, 3, 255, "rgba(1, 2, 3, 1)")]
    [InlineData("rgb(255 136 0)", 1, 2, 3, 128, "rgb(1 2 3 / 0.5)")]
    [InlineData("rgb(255 136 0 / 50%)", 1, 2, 3, 64, "rgb(1 2 3 / 25%)")]
    [InlineData("hsl(120, 100%, 50%)", 255, 0, 0, 255, "hsl(0, 100%, 50%)")]
    [InlineData("hsl(120 100% 50%)", 0, 0, 255, 128, "hsl(240 100% 50% / 0.5)")]
    public void Writes_back_in_the_original_style(string original, int r, int g, int b, int a, string expected)
    {
        var literal = Assert.Single(Find(original));

        Assert.Equal(expected, ColorLiterals.Format(new RgbaColor((byte)r, (byte)g, (byte)b, (byte)a), literal.Style));
    }

    [Fact]
    public void Switching_format_cycles_hex_rgb_hsl_and_remembers_hex_case()
    {
        var literal = Assert.Single(Find("#FF8800"));
        var rgb = ColorLiterals.Switch(literal.Style, ColorLiterals.Next(ColorFormat.Hex));
        var hsl = ColorLiterals.Switch(rgb, ColorLiterals.Next(rgb.Format));
        var hex = ColorLiterals.Switch(hsl, ColorLiterals.Next(hsl.Format));

        Assert.Equal("rgb(255, 136, 0)", ColorLiterals.Format(literal.Color, rgb));
        Assert.Equal("hsl(32, 100%, 50%)", ColorLiterals.Format(literal.Color, hsl));
        Assert.Equal("#FF8800", ColorLiterals.Format(literal.Color, hex));
    }

    [Fact]
    public void Parses_a_whole_field_and_rejects_partial_text()
    {
        Assert.True(ColorLiterals.TryParse("  #0af ", Css, out var hex));
        Assert.Equal(new RgbaColor(0, 170, 255), hex.Color);
        Assert.True(ColorLiterals.TryParse("hsl(0 0% 100%)", Css, out var white));
        Assert.Equal(new RgbaColor(255, 255, 255), white.Color);
        Assert.False(ColorLiterals.TryParse("#0af;", Css, out _));
        Assert.False(ColorLiterals.TryParse("blue", Css, out _));
    }

    [Theory]
    [InlineData(255, 0, 0)]
    [InlineData(18, 52, 86)]
    [InlineData(200, 200, 200)]
    [InlineData(0, 0, 0)]
    [InlineData(123, 222, 45)]
    public void Hsv_and_hsl_round_trip(int r, int g, int b)
    {
        var color = new RgbaColor((byte)r, (byte)g, (byte)b, 77);

        Assert.Equal(color, color.ToHsv().ToRgba());
        Assert.Equal(color, color.ToHsl().ToRgba());
    }

    [Fact]
    public void Hsv_components_are_as_expected()
    {
        var hsv = new RgbaColor(255, 128, 0).ToHsv();

        Assert.Equal(30, hsv.H, 0.5);
        Assert.Equal(1, hsv.S, 3);
        Assert.Equal(1, hsv.V, 3);
    }

    [Fact]
    public void Languages_are_enabled_selectively()
    {
        Assert.True(ColorLiterals.IsEnabledFor("css"));
        Assert.True(ColorLiterals.IsEnabledFor("csharp"));
        Assert.True(ColorLiterals.IsEnabledFor("typescriptreact"));
        Assert.False(ColorLiterals.IsEnabledFor("plaintext"));
        Assert.False(ColorLiterals.IsEnabledFor("python"));
    }
}
