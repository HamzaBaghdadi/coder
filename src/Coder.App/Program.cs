using Avalonia;
using Coder.App.Services;
using Coder.Presentation.Settings;

namespace Coder.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // The installer, updater and uninstaller start Coder just to run these, then it exits: the "coder"
        // terminal command and File Explorer's "Open with Coder" come with installing (and with each update,
        // so earlier installs get them too) and go with uninstalling.
        var app = Velopack.VelopackApp.Build();
        if (OperatingSystem.IsWindows())
        {
            app = app.OnAfterInstallFastCallback(_ => SetUpShell(install: true))
                .OnAfterUpdateFastCallback(_ => SetUpShell(install: true))
                .OnBeforeUninstallFastCallback(_ => SetUpShell(install: false));
        }

        app.Run();

        var (userDataDir, paths) = ParseArguments(args);
        var settingsPath = JsonSettingsStore.DefaultPath;
        if (userDataDir is not null)
        {
            settingsPath = Path.Combine(userDataDir, "settings.json");
            CrashReporter.LogDirectory = Path.Combine(userDataDir, "logs");
        }

        return BuildAvaloniaApp(new JsonSettingsStore(settingsPath)).StartWithClassicDesktopLifetime(paths);
    }

    /// <summary>An installer hook: must be quick and must never fail the install.</summary>
    private static void SetUpShell(bool install)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var shell = ShellIntegration.ForThisApp();
            _ = install ? shell.Install() : shell.Uninstall();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Coder works without it; the command palette can try again.
        }
    }

    /// <summary>
    /// <c>coder [--user-data-dir &lt;folder&gt;] [paths...]</c>. The data folder keeps settings and logs
    /// apart from the default profile (a portable install, or a second instance for testing).
    /// </summary>
    private static (string? UserDataDir, string[] Paths) ParseArguments(string[] args)
    {
        string? userDataDir = null;
        var paths = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--user-data-dir" && i + 1 < args.Length)
            {
                userDataDir = Path.GetFullPath(args[++i]);
            }
            else
            {
                paths.Add(args[i]);
            }
        }

        return (userDataDir, paths.ToArray());
    }

    /// <summary>
    /// The renderer is chosen before the platform starts, so the "GPU acceleration" preference is read
    /// here. GPU rendering scrolls smoother; software rendering roughly halves memory use.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp(ISettingsStore settings)
    {
        var builder = AppBuilder.Configure(() => new App(settings))
            .UsePlatformDetect()
            .LogToTrace();

        if (settings.Load().GpuAcceleration)
        {
            return builder;
        }

        return builder
            .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] })
            .With(new X11PlatformOptions { RenderingMode = [X11RenderingMode.Software] })
            .With(new AvaloniaNativePlatformOptions { RenderingMode = [AvaloniaNativeRenderingMode.Software] });
    }
}
