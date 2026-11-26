using Avalonia;
using Avalonia.Headless;
using Coder.App.Services;
using Coder.App.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Coder.App.Tests;

/// <summary>Runs the real <see cref="App"/> (theme and styles) on the headless platform with Skia rendering.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure(() => new App(new JsonSettingsStore(Path.Combine(Path.GetTempPath(), "coder-tests", "settings.json"))))
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
