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
                    Accent = Color.Parse("#3D8BFF"),
                    RegionColor = Color.Parse("#0C0F17"),
                    AltHigh = Color.Parse("#0C0F17"),
                    BaseHigh = Color.Parse("#D5DBE5"),
                    ChromeMedium = Color.Parse("#121826"),
                    ChromeMediumLow = Color.Parse("#0F1420"),
                },
                [ThemeVariant.Light] = new ColorPaletteResources
                {
                    Accent = Color.Parse("#1A62D6"),
                    RegionColor = Color.Parse("#FBFCFE"),
                    AltHigh = Color.Parse("#FBFCFE"),
                    BaseHigh = Color.Parse("#1A2332"),
                    ChromeMedium = Color.Parse("#FFFFFF"),
                    ChromeMediumLow = Color.Parse("#F1F4F8"),
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
