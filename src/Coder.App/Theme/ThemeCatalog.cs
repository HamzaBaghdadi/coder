using Coder.Core.Themes;
using Coder.Presentation.Settings;

namespace Coder.App.Theme;

/// <summary>
/// The VS Code themes on this machine (built into VS Code, from installed extensions, and theme files in
/// Coder's themes folder), found when the theme picker opens and turned into Coder themes when chosen.
/// Converted themes are kept, so previewing through the list stays quick.
/// </summary>
public sealed class ThemeCatalog(IReadOnlyList<string> extensionFolders, string? themesFolder) : IColorThemeCatalog
{
    private readonly Dictionary<string, ColorTheme> _converted = [];
    private IReadOnlyList<VsCodeThemeEntry>? _entries;

    /// <summary>The folder for loose theme files (<c>&lt;user data&gt;/themes</c>).</summary>
    public string? ThemesFolder => themesFolder;

    public async Task<IReadOnlyList<ColorThemeChoice>> FindAsync()
    {
        var entries = await Task.Run(() => VsCodeThemeCatalog.Discover(extensionFolders, themesFolder));
        _entries = entries;
        _converted.Clear(); // a theme file may have been edited since
        return entries.Select(entry => new ColorThemeChoice(entry.Id, entry.Label, entry.Source)).ToList();
    }

    /// <summary>The theme with this id, converted from its VS Code file; null when it isn't found or can't be read.</summary>
    public ColorTheme? Resolve(string id)
    {
        if (_converted.TryGetValue(id, out var theme))
        {
            return theme;
        }

        _entries ??= VsCodeThemeCatalog.Discover(extensionFolders, themesFolder);
        if (_entries.FirstOrDefault(e => e.Id == id) is not { } entry)
        {
            return null;
        }

        try
        {
            theme = VsCodeThemeConverter.Convert(VsCodeTheme.Load(entry.Path), entry);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }

        _converted[id] = theme;
        return theme;
    }
}
