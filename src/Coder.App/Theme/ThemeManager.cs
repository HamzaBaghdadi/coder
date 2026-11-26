using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Coder.Presentation.Workbench;

namespace Coder.App.Theme;

/// <summary>
/// Applies color themes: the tokens (<see cref="Palette"/>), the Fluent controls' own variant, and a
/// repaint of custom-drawn views. It follows the workbench's chosen theme, and with "Follow System"
/// the operating system's light/dark setting as it changes.
/// </summary>
public static class ThemeManager
{
    public const string FollowSystem = "system";

    private static ThemeCatalog? _catalog;

    /// <summary>Raised after a theme was applied; windows repaint, overlays refresh their shadows.</summary>
    public static event EventHandler? Changed;

    /// <summary>
    /// Applies the workbench's theme now and whenever it (or, for "system", the OS) changes. VS Code themes
    /// come from <paramref name="catalog"/>; one that can't be found or read falls back to Midnight Blue.
    /// </summary>
    public static void Follow(WorkbenchViewModel workbench, ThemeCatalog? catalog = null)
    {
        _catalog = catalog;
        void ApplyChosen()
        {
            var id = workbench.ColorTheme;
            var theme = Resolve(id);
            if (theme.Id != id && id is not FollowSystem)
            {
                workbench.StatusBar.Message = $"The color theme \"{id}\" couldn't be loaded, so Midnight Blue is used.";
            }

            Apply(theme);
        }

        ApplyChosen();
        workbench.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorkbenchViewModel.ColorTheme))
            {
                ApplyChosen();
            }
        };

        if (Application.Current?.PlatformSettings is { } platform)
        {
            platform.ColorValuesChanged += (_, _) =>
            {
                if (workbench.ColorTheme == FollowSystem)
                {
                    Apply(Resolve(FollowSystem));
                }
            };
        }
    }

    /// <summary>The theme for a setting: a built-in theme's id, "system" (the OS preference), or a VS Code theme's id.</summary>
    public static ColorTheme Resolve(string? id)
    {
        if (id != FollowSystem)
        {
            return ColorThemes.Find(id) ?? (id is not null ? _catalog?.Resolve(id) : null) ?? ColorThemes.MidnightBlue;
        }

        // Windows' contrast themes ask for high contrast: honor it, in the matching brightness.
        var values = Application.Current?.PlatformSettings?.GetColorValues();
        var light = values?.ThemeVariant == PlatformThemeVariant.Light;
        if (values?.ContrastPreference == ColorContrastPreference.High)
        {
            return light ? ColorThemes.HighContrastLight : ColorThemes.HighContrastDark;
        }

        return light ? ColorThemes.Daylight : ColorThemes.MidnightBlue;
    }

    public static void Apply(ColorTheme theme)
    {
        Palette.Apply(theme);
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = theme.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>An overlay shadow (<c>"0 12 32 0"</c>) in the theme's shadow color: deep on dark, soft on light.</summary>
    public static BoxShadows Shadow(string geometry) => BoxShadows.Parse($"{geometry} {Palette.Shadow.Color}");

    /// <summary>Keeps a control's shadow in the theme's color.</summary>
    public static void BindShadow(Avalonia.Controls.Border border, string geometry)
    {
        border.BoxShadow = Shadow(geometry);
        Changed += (_, _) => border.BoxShadow = Shadow(geometry);
    }
}
