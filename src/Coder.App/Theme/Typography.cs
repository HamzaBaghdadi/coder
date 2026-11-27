using Avalonia;
using Avalonia.Media;

namespace Coder.App.Theme;

/// <summary>
/// Font tokens. The type scale suits a desktop tool read for hours: 13px body (the norm for code
/// editors), stepping up by about 1.25 for titles and headings. Section headers use sentence case in
/// semibold rather than spaced capitals, which read faster.
/// </summary>
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

    /// <summary>Well-known coding fonts, offered by the Font Family setting when installed.</summary>
    private static readonly string[] KnownCodeFonts =
    [
        "JetBrains Mono", "Cascadia Code", "Cascadia Mono", "Fira Code", "Fira Mono", "Source Code Pro", "Hack", "Iosevka",
        "IBM Plex Mono", "Roboto Mono", "Ubuntu Mono", "DejaVu Sans Mono", "Victor Mono", "Monaspace Neon", "Geist Mono",
        "Consolas", "Lucida Console", "Courier New", "SF Mono", "Menlo", "Monaco",
    ];

    /// <summary>The coding fonts installed on this machine, in <see cref="KnownCodeFonts"/> order.</summary>
    public static IReadOnlyList<string> InstalledCodeFonts()
    {
        var installed = FontManager.Current.SystemFonts.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return KnownCodeFonts.Where(installed.Contains).ToList();
    }

    /// <summary>The coding font with its fallbacks, for text controls showing code (find, search fields).</summary>
    public static readonly FontFamily Code = new(string.Join(", ", CodeFamilies.Concat(CodeFallbackFamilies)));

    public const double CaptionSize = 11;  // badges, key hints, secondary metadata
    public const double SmallSize = 12;    // status bar, tooltips, list details
    public const double UiSize = 13;       // body: lists, buttons, fields
    public const double TitleSize = 15;    // panel and dialog titles, settings groups
    public const double HeadingSize = 20;  // page headings (Settings, extension details)
    public const double DisplaySize = 28;  // the welcome page's name

    /// <summary>Weight of headers and titles; body text stays regular.</summary>
    public static readonly FontWeight Strong = FontWeight.SemiBold;

    /// <summary>Editor line height as a multiple of the font size.</summary>
    public const double LineHeightFactor = 1.5;
}

/// <summary>Spacing, shape and size tokens on a 4px grid. A dense grid suits a tool used for hours a day.</summary>
public static class Spacing
{
    public const double XXS = 2;
    public const double XS = 4;
    public const double S = 8;
    public const double M = 12;
    public const double L = 16;
    public const double XL = 24;
    public const double XXL = 32;

    public static readonly CornerRadius RadiusSmall = new(4);   // toggles, key caps, inline code, badges
    public static readonly CornerRadius RadiusMedium = new(6);  // buttons, fields, list rows, tabs
    public static readonly CornerRadius RadiusPanel = new(8);   // the inset editor, panel and secondary side bar
    public static readonly CornerRadius RadiusLarge = new(10);  // overlays: palette, menus, find, dialogs, cards

    /// <summary>The gap around and between the inset panels; also the splitters' grab area.</summary>
    public const double PanelGap = 4;

    /// <summary>Cut-corner sizes (see Controls/ChamferBorder). The UI is rounded throughout; only the logo art
    /// keeps the angular shape, so these are for brand artwork, not controls.</summary>
    public const double ChamferSmall = 4;
    public const double ChamferMedium = 7;
    public const double ChamferLarge = 10;

    /// <summary>Fixed chrome sizes.</summary>
    public const double TitleBarHeight = 38;

    /// <summary>
    /// Title bar buttons share one square size. Glyphs fill their 24px grid differently, so each kind gets the
    /// size that makes it look the same: the cog and account outline at 17, the layout frames (drawn smaller in
    /// their grid) at 19, and the account picture at 20.
    /// </summary>
    public const double TitleBarButton = 30;
    public const double TitleBarGlyph = 17;
    public const double TitleBarLayoutGlyph = 19;
    public const double TitleBarAvatar = 20;
    public const double ActivityBarWidth = 48;
    public const double TabHeight = 34;
    public const double StatusBarHeight = 24;
    public const double ListRowHeight = 24;
    public const double SideBarWidth = 260;
}
