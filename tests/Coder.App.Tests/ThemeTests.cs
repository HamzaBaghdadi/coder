using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Coder.App.Services;
using Coder.App.Theme;
using Coder.Core.Syntax;
using Coder.Core.Terminal;

namespace Coder.App.Tests;

public sealed class ThemeTests
{
    public static TheoryData<string> Themes => new(ColorThemes.All.Select(t => t.Id));

    /// <remarks>Brushes are Avalonia objects: this runs on the Avalonia test thread, like the app's UI thread.</remarks>
    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public void Every_theme_defines_exactly_the_tokens_in_use(string id)
    {
        RegisterAllTokens();
        var theme = ColorThemes.ById(id);

        Assert.Empty(Palette.TokenNames.Except(theme.TokenNames));   // nothing missing
        Assert.Empty(theme.TokenNames.Except(Palette.TokenNames));   // no typos or leftovers
    }

    /// <summary>WCAG AA for text: 4.5:1 for everything a user reads, on the editor and on the side bar.</summary>
    [Theory]
    [MemberData(nameof(Themes))]
    public void Text_and_syntax_colors_stay_readable(string id)
    {
        var theme = ColorThemes.ById(id);
        string[] readable =
        [
            "TextPrimary", "TextSecondary", "TextMuted", "Accent",
            "Syntax.Keyword", "Syntax.Type", "Syntax.Function", "Syntax.String", "Syntax.Number",
            "Syntax.Comment", "Syntax.Constant", "Syntax.Operator", "Syntax.Punctuation", "Syntax.Preprocessor",
            "GitModified", "GitAdded", "GitDeleted", "DiagnosticError",
        ];

        var failures =
            from surface in new[] { "EditorBackground", "SideBar" }
            from token in readable
            let ratio = Contrast(Color.Parse(theme[token]), Color.Parse(theme[surface]))
            where ratio < 4.5
            select $"{token} on {surface}: {ratio:F2}:1";

        Assert.Empty(failures); // the message lists every pair below 4.5:1
    }

    [Fact]
    public void Settings_with_missing_keys_load_with_the_defaults()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("coder-settings-").FullName, "settings.json");
        File.WriteAllText(file, "{}");

        var settings = new JsonSettingsStore(file).Load();

        var defaults = new Presentation.Settings.UserSettings();
        Assert.Equal(defaults.EditorFontSize, settings.EditorFontSize);
        Assert.Equal(defaults.SideBarWidth, settings.SideBarWidth);
        Assert.True(settings.IsSideBarVisible);
        Assert.True(settings.GpuAcceleration);
        Assert.Equal("dark", settings.ColorTheme);
        Assert.Empty(settings.RecentFolders);
        Assert.Empty(settings.LanguageServers);
    }

    /// <summary>
    /// Every VS Code theme on this machine (VS Code's own and installed extensions') loads and converts to
    /// a theme whose every token is a valid color. Skipped where VS Code isn't installed.
    /// </summary>
    [AvaloniaFact]
    public void Every_installed_vs_code_theme_converts()
    {
        RegisterAllTokens();
        var entries = Coder.Core.Themes.VsCodeThemeCatalog.Discover(Coder.Core.Themes.VsCodeThemeCatalog.DefaultExtensionFolders(), null);
        if (entries.Count == 0)
        {
            Assert.Skip("No VS Code themes on this machine.");
            return;
        }

        var failures = new List<string>();
        foreach (var entry in entries)
        {
            try
            {
                var theme = VsCodeThemeConverter.Convert(Coder.Core.Themes.VsCodeTheme.Load(entry.Path), entry);
                failures.AddRange(Palette.TokenNames.Where(token => !VsCodeThemeConverter.IsColor(theme[token])).Select(token => $"{entry.Label}: {token} = {theme[token]}"));
            }
            catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException)
            {
                failures.Add($"{entry.Label} ({entry.Path}): {exception.Message}");
            }
        }

        Assert.Empty(failures);
    }

    /// <summary>Syntax and terminal tokens register when their classes first load.</summary>
    private static void RegisterAllTokens()
    {
        _ = SyntaxColors.For(TokenKind.Keyword);
        _ = TerminalPalette.Resolve(new TerminalColor(TerminalColorKind.Indexed, 1), Palette.TextPrimary);
    }

    private static double Contrast(Color a, Color b)
    {
        static double Luminance(Color c)
        {
            static double Channel(byte v)
            {
                var s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        }

        var (light, dark) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (light + 0.05) / (dark + 0.05);
    }
}
