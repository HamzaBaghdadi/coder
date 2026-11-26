using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Coder.App.Theme;
using Coder.App.Workbench;
using Coder.Core.Files;
using Coder.Presentation.Workbench;

namespace Coder.App.Tests;

public sealed partial class WorkbenchUiTests
{
    /// <summary>
    /// Switching themes from the palette recolors everything live, including the custom-drawn editor
    /// (checked on rendered pixels, not just on the brushes), and switching back restores the dark theme.
    /// </summary>
    [AvaloniaFact]
    public async Task Color_theme_switches_live_from_the_palette()
    {
        var (window, workbench) = CreateWindow();
        ThemeManager.Follow(workbench);
        try
        {
            workbench.OpenFolder(RepositoryRoot);
            window.Show();
            await workbench.OpenFileAsync(Path.Combine(RepositoryRoot, "src", "Coder.Core", "Text", "PieceTable.cs"));
            workbench.ShowPanel(PanelView.Problems);
            Dispatcher.UIThread.RunJobs();
            Assert.True(EditorBrightness(window) < 60, "starts dark");

            // Ctrl+K Ctrl+T opens the picker (a chord, as in VS Code).
            window.KeyPressQwerty(PhysicalKey.K, RawInputModifiers.Control);
            window.KeyPressQwerty(PhysicalKey.T, RawInputModifiers.Control);
            await WaitUntil(() => workbench.Palette.IsOpen && workbench.Palette.Items.Count >= 5, () => $"open: {workbench.Palette.IsOpen}");

            // Moving through the list previews each theme; Esc goes back.
            workbench.Palette.SelectedItem = workbench.Palette.Items.Single(i => i.Label == "High Contrast Dark");
            Assert.Same(ColorThemes.HighContrastDark, Palette.Current);
            Save(window, "theme-high-contrast");
            workbench.Palette.Close();
            Assert.Equal("dark", workbench.ColorTheme);
            Assert.Same(ColorThemes.MidnightBlue, Palette.Current);

            await workbench.ShowThemePickerAsync();
            await workbench.Palette.AcceptAsync(workbench.Palette.Items.Single(i => i.Label == "Daylight"));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("light", workbench.ColorTheme);
            Assert.Same(ColorThemes.Daylight, Palette.Current);
            Assert.Equal(ThemeVariant.Light, Application.Current!.RequestedThemeVariant);
            Assert.True(EditorBrightness(window) > 220, "the editor repainted light");
            Save(window, "theme-light");

            workbench.ShowSideBarView(SideBarView.Search);
            workbench.Search.Query = "TryExtendPreviousInsert";
            await WaitUntil(() => !workbench.Search.IsSearching && workbench.Search.Rows.Count > 0);
            Save(window, "theme-light-search");

            workbench.ColorTheme = "dark";
            Dispatcher.UIThread.RunJobs();
            Assert.True(EditorBrightness(window) < 60, "back to dark");
        }
        finally
        {
            workbench.ColorTheme = "dark";
            window.Close();
        }
    }

    /// <summary>
    /// A VS Code theme from an installed extension and one from Coder's themes folder show up in the
    /// picker; choosing one recolors the workbench from its colors and syntax rules, the rest falling back
    /// to the built-in theme of its kind. The choice survives a restart (resolved again from its id).
    /// </summary>
    [AvaloniaFact]
    public async Task Vs_code_themes_are_found_and_applied()
    {
        var root = Directory.CreateTempSubdirectory("coder-vscode-themes-").FullName;
        var extension = Directory.CreateDirectory(Path.Combine(root, "extensions", "someone.ember-1.0.0", "themes")).Parent!.FullName;
        File.WriteAllText(Path.Combine(extension, "package.json"), """
            { "name": "ember", "publisher": "someone", "displayName": "Ember Themes",
              "contributes": { "themes": [ { "label": "Ember", "uiTheme": "vs-dark", "path": "./themes/ember.json" } ] } }
            """);
        File.WriteAllText(Path.Combine(extension, "themes", "ember.json"), """
            {
              // A made-up theme with only some keys set.
              "colors": { "editor.background": "#201510", "editor.foreground": "#F0E0D0", "sideBar.background": "#181008", "list.hoverBackground": "#30201880" },
              "tokenColors": [
                { "scope": "keyword", "settings": { "foreground": "#FF9050" } },
                { "scope": ["string"], "settings": { "foreground": "#A0D070" } },
              ],
            }
            """);
        var themesFolder = Directory.CreateDirectory(Path.Combine(root, "themes")).FullName;
        File.WriteAllText(Path.Combine(themesFolder, "paper.json"), """{ "name": "Paper", "type": "light", "colors": { "editor.background": "#FAF8F0" } }""");
        var catalog = new ThemeCatalog([Path.Combine(root, "extensions")], themesFolder);

        var settings = new MemorySettingsStore();
        var workbench = new WorkbenchViewModel(new LocalFileSystem(), new NoDialogs(), settings, themes: catalog);
        var window = new MainWindow(workbench) { Width = 1280, Height = 800 };
        ThemeManager.Follow(workbench, catalog);
        try
        {
            workbench.OpenFolder(RepositoryRoot);
            window.Show();
            await workbench.OpenFileAsync(Path.Combine(RepositoryRoot, "src", "Coder.Core", "Text", "PieceTable.cs"));

            await workbench.ShowThemePickerAsync();
            var ember = workbench.Palette.Items.Single(i => i.Label == "Ember");
            Assert.Equal("Ember Themes", ember.Detail);
            Assert.Contains(workbench.Palette.Items, i => i.Label == "Paper" && i.Detail == "Theme file");
            await workbench.Palette.AcceptAsync(ember);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("vscode:someone.ember/Ember", workbench.ColorTheme);
            Assert.Equal("Ember", Palette.Current.Name);
            Assert.Equal(Avalonia.Media.Color.Parse("#201510"), Palette.EditorBackground.Color);
            Assert.Equal(Avalonia.Media.Color.Parse("#181008"), Palette.SideBar.Color);
            Assert.Equal(Avalonia.Media.Color.Parse("#FF9050"), ((Avalonia.Media.ISolidColorBrush)SyntaxColors.For(Coder.Core.Syntax.TokenKind.Keyword)).Color);
            Assert.Equal(Avalonia.Media.Color.Parse("#00302018"), Palette.HoverClear.Color);                   // derived from the hover color
            Assert.Equal(Avalonia.Media.Color.Parse("#201510"), Palette.Elevated.Color);                       // unset surface: the theme's background
            Assert.Equal(ColorThemes.MidnightBlue["Syntax.Comment"], Palette.Current["Syntax.Comment"]); // not set: the dark base
            Save(window, "theme-vscode");

            // Paper is light: its base is Daylight.
            await workbench.ShowThemePickerAsync();
            await workbench.Palette.AcceptAsync(workbench.Palette.Items.Single(i => i.Label == "Paper"));
            Assert.False(Palette.Current.IsDark);
            Assert.Equal(ColorThemes.Daylight["TextPrimary"], Palette.Current["TextPrimary"]);

            // A theme that has gone (extension uninstalled) falls back to Midnight Blue and says so.
            workbench.ColorTheme = "vscode:someone.gone/Gone";
            Assert.Same(ColorThemes.MidnightBlue, Palette.Current);
            Assert.Contains("couldn't be loaded", workbench.StatusBar.Message, StringComparison.Ordinal);
        }
        finally
        {
            workbench.ColorTheme = "dark";
            window.Close();
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Average brightness (0–255) of an empty patch of the editor, right of the code.</summary>
    private static double EditorBrightness(MainWindow window)
    {
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame()!;
        var (width, height) = (frame.PixelSize.Width, frame.PixelSize.Height);
        var stride = width * 4;
        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(stride * height);
        try
        {
            frame.CopyPixels(new PixelRect(0, 0, width, height), buffer, stride * height, stride);
            var pixels = new byte[stride * height];
            System.Runtime.InteropServices.Marshal.Copy(buffer, pixels, 0, pixels.Length);

            var total = 0.0;
            var count = 0;
            for (var x = 1100; x < 1200; x += 20)
            {
                for (var y = 400; y < 450; y += 10)
                {
                    var i = y * stride + x * 4;
                    total += (pixels[i] + pixels[i + 1] + pixels[i + 2]) / 3.0;
                    count++;
                }
            }

            return total / count;
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }
    }
}
