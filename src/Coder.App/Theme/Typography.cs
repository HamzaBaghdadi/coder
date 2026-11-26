using Avalonia;
using Avalonia.Media;

namespace Coder.App.Theme;

/// <summary>Font and spacing tokens. A dense 4px spacing grid suits a tool used for hours a day.</summary>
public static class Typography
{
    /// <summary>System UI font first, so the app feels native on every OS.</summary>
    public static readonly FontFamily Ui = new("Segoe UI Variable Text, Segoe UI, SF Pro Text, Inter, Ubuntu, Cantarell, sans-serif");

    /// <summary>Coding fonts in order of preference; the first installed one is used.</summary>
    public static readonly IReadOnlyList<string> CodeFamilies =
        ["JetBrains Mono", "Cascadia Code", "Cascadia Mono", "Fira Code", "SF Mono", "Menlo", "Consolas", "DejaVu Sans Mono", "monospace"];

    /// <summary>
    /// Fonts for scripts coding fonts lack, tried in order for each such character: Arabic,
    /// Chinese/Japanese/Korean and emoji, for Windows, macOS and Linux (see Editor/GlyphFallback).
    /// </summary>
    public static readonly IReadOnlyList<string> CodeFallbackFamilies =
    [
        "Segoe UI", "Geeza Pro", "Noto Sans Arabic",                                                           // Arabic
        "Microsoft YaHei UI", "Yu Gothic UI", "Malgun Gothic", "PingFang SC", "Hiragino Sans", "Noto Sans CJK SC", // CJK
        "Segoe UI Emoji", "Apple Color Emoji", "Noto Color Emoji",                                             // emoji
    ];

    /// <summary>The coding font with its fallbacks, for text controls showing code (find, search fields).</summary>
    public static readonly FontFamily Code = new(string.Join(", ", CodeFamilies.Concat(CodeFallbackFamilies)));

    public const double UiSize = 13;
    public const double SmallSize = 12;
    public const double CaptionSize = 11;

    /// <summary>Editor line height as a multiple of the font size.</summary>
    public const double LineHeightFactor = 1.5;
}

public static class Spacing
{
    public const double XS = 4;
    public const double S = 8;
    public const double M = 12;
    public const double L = 16;
    public const double XL = 24;

    public static readonly CornerRadius RadiusMedium = new(6);
    public static readonly CornerRadius RadiusLarge = new(10);

    /// <summary>Cut-corner sizes for the brand's angular shapes (see Controls/ChamferBorder).</summary>
    public const double ChamferSmall = 4;
    public const double ChamferMedium = 7;
    public const double ChamferLarge = 10;

    /// <summary>Fixed chrome sizes.</summary>
    public const double TitleBarHeight = 36;
    public const double ActivityBarWidth = 48;
    public const double TabHeight = 36;
    public const double StatusBarHeight = 24;
    public const double ListRowHeight = 24;
    public const double SideBarWidth = 260;
}
