using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Coder.App.Theme;
using Coder.Presentation.Settings;

namespace Coder.App;

/// <summary>The Avalonia application: theme setup and creation of the main window.</summary>
public sealed class App(ISettingsStore settings) : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme
        {
            // Fluent's own controls (scroll bars, text boxes, focus rings) follow the app palette
            // instead of the OS accent color.
            Palettes =
            {
                [ThemeVariant.Dark] = new ColorPaletteResources
                {
                    Accent = Color.Parse("#4C8DFF"),
                    RegionColor = Color.Parse("#111215"),
                    AltHigh = Color.Parse("#111215"),
                    BaseHigh = Color.Parse("#E3E5EA"),
                    ChromeMedium = Color.Parse("#181A1F"),
                    ChromeMediumLow = Color.Parse("#0B0C0E"),
                },
                [ThemeVariant.Light] = new ColorPaletteResources
                {
                    Accent = Color.Parse("#1F62D6"),
                    RegionColor = Color.Parse("#FFFFFF"),
                    AltHigh = Color.Parse("#FFFFFF"),
                    BaseHigh = Color.Parse("#1B1D22"),
                    ChromeMedium = Color.Parse("#FFFFFF"),
                    ChromeMediumLow = Color.Parse("#F3F4F6"),
                },
            },
        });
        AppStyles.Apply(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = CompositionRoot.CreateMainWindow(desktop.Args ?? [], settings);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
