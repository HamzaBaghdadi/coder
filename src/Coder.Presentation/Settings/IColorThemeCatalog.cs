namespace Coder.Presentation.Settings;

/// <summary>A color theme beyond the built-in ones, as the theme picker lists it.</summary>
/// <param name="Id">What <c>ColorTheme</c> stores to choose it.</param>
/// <param name="Detail">Where it comes from ("Built into VS Code", an extension's name, "Theme file").</param>
public sealed record ColorThemeChoice(string Id, string Label, string Detail);

/// <summary>Finds the color themes that can be added to the built-in ones (VS Code themes on this machine).</summary>
public interface IColorThemeCatalog
{
    /// <summary>Looks again each time, so newly installed themes show up.</summary>
    Task<IReadOnlyList<ColorThemeChoice>> FindAsync();
}
