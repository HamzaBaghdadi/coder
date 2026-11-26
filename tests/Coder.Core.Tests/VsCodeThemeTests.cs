using Coder.Core.Themes;

namespace Coder.Core.Tests;

public sealed class VsCodeThemeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("coder-themes-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("#abc", "#FFAABBCC")]
    [InlineData("#abcd", "#DDAABBCC")]
    [InlineData("#1E1E1E", "#FF1E1E1E")]
    [InlineData("#264f7880", "#80264F78")]
    [InlineData("red", null)]
    [InlineData("#12345", null)]
    [InlineData("#GGGGGG", null)]
    public void Normalizes_css_hex_colors_to_argb(string value, string? expected) =>
        Assert.Equal(expected, VsCodeTheme.NormalizeColor(value));

    [Fact]
    public void Reads_jsonc_colors_and_token_rules()
    {
        const string Json = """
            // VS Code themes are JSON with comments
            {
              "name": "Night Owl", "type": "dark",
              "colors": { "editor.background": "#011627", "editor.foreground": "#d6deeb", "bad": 3, },
              "tokenColors": [
                { "settings": { "foreground": "#D6DEEB" } },
                { "scope": "keyword", "settings": { "foreground": "#c792ea" } },
                { "scope": ["keyword.operator", "punctuation"], "settings": { "foreground": "#7fdbca" } },
                { "scope": "string, string.quoted", "settings": { "foreground": "#ecc48d", "fontStyle": "italic" } },
                { "scope": "source.python keyword", "settings": { "foreground": "#ff0000" } },
                { "scope": "keyword", "settings": { "foreground": "#c792eb" } },
              ],
            }
            """;

        var theme = VsCodeTheme.Parse(Json);

        Assert.Equal(("Night Owl", "dark"), (theme.Name, theme.Type));
        Assert.Equal("#FF011627", theme.Colors["editor.background"]);
        Assert.False(theme.Colors.ContainsKey("bad"));
        Assert.Equal("#FFD6DEEB", theme.DefaultForeground);
        Assert.Equal("#FFC792EB", theme.Foreground("keyword.control.if"));     // the later of two equal rules
        Assert.Equal("#FF7FDBCA", theme.Foreground("keyword.operator.arithmetic")); // the more specific selector
        Assert.Equal("#FFECC48D", theme.Foreground("string.quoted.double"));
        Assert.Null(theme.Foreground("keywords"));                                // "keyword" is not a prefix at a dot
        Assert.Null(theme.Foreground("comment"));
    }

    [Fact]
    public void An_included_theme_is_the_base_and_token_colors_can_live_in_another_file()
    {
        File.WriteAllText(Path.Combine(_root, "base.json"), """{ "type": "light", "colors": { "editor.background": "#FFFFFF", "editor.foreground": "#000000" }, "tokenColors": [ { "scope": "comment", "settings": { "foreground": "#008000" } } ] }""");
        File.WriteAllText(Path.Combine(_root, "tokens.json"), """{ "tokenColors": [ { "scope": "string", "settings": { "foreground": "#A31515" } } ] }""");
        File.WriteAllText(Path.Combine(_root, "mine.json"), """{ "include": "./base.json", "name": "Mine", "colors": { "editor.foreground": "#333333" }, "tokenColors": "./tokens.json" }""");

        var theme = VsCodeTheme.Load(Path.Combine(_root, "mine.json"));

        Assert.Equal(("Mine", "light"), (theme.Name, theme.Type));
        Assert.Equal("#FFFFFFFF", theme.Colors["editor.background"]);
        Assert.Equal("#FF333333", theme.Colors["editor.foreground"]);
        Assert.Equal("#FF008000", theme.Foreground("comment.line"));
        Assert.Equal("#FFA31515", theme.Foreground("string"));
    }

    [Fact]
    public void Finds_themes_in_extensions_and_in_the_themes_folder()
    {
        // Two versions of one extension (the newer wins), a localized label, and a loose theme file.
        foreach (var version in new[] { "1.0.0", "1.1.0" })
        {
            var extension = Directory.CreateDirectory(Path.Combine(_root, "extensions", $"sdras.night-owl-{version}")).FullName;
            Directory.CreateDirectory(Path.Combine(extension, "themes"));
            File.WriteAllText(Path.Combine(extension, "themes", "owl.json"), "{}");
            File.WriteAllText(Path.Combine(extension, "package.json"), $$"""
                { "name": "night-owl", "publisher": "sdras", "displayName": "Night Owl {{version}}",
                  "contributes": { "themes": [ { "label": "%owl.label%", "uiTheme": "vs-dark", "path": "./themes/owl.json" } ] } }
                """);
            File.WriteAllText(Path.Combine(extension, "package.nls.json"), """{ "owl.label": { "message": "Night Owl", "comment": [] } }""");
        }

        Directory.CreateDirectory(Path.Combine(_root, "extensions", "no-manifest"));
        var themes = Directory.CreateDirectory(Path.Combine(_root, "themes")).FullName;
        File.WriteAllText(Path.Combine(themes, "paper.json"), """{ "name": "Paper", "type": "light" }""");
        File.WriteAllText(Path.Combine(themes, "broken.json"), "{ not json");

        var entries = VsCodeThemeCatalog.Discover([Path.Combine(_root, "extensions"), Path.Combine(_root, "missing")], themes);

        Assert.Equal(2, entries.Count);
        var owl = entries.Single(e => e.Label == "Night Owl");
        Assert.Equal("vscode:sdras.night-owl/Night Owl", owl.Id);
        Assert.Equal("Night Owl 1.1.0", owl.Source);
        Assert.True(owl.IsDark);
        Assert.EndsWith(Path.Combine("sdras.night-owl-1.1.0", "themes", "owl.json"), owl.Path, StringComparison.Ordinal);
        var paper = entries.Single(e => e.Label == "Paper");
        Assert.Equal(("file:paper.json", "vs", "Theme file"), (paper.Id, paper.UiTheme, paper.Source));
    }
}
