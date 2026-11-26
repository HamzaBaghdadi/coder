using Coder.App.Services;
using Coder.App.Theme;
using Coder.App.Workbench;
using Coder.Core.Extensions;
using Coder.Core.Files;
using Coder.Core.Lsp;
using Coder.Core.Themes;
using Coder.Presentation.Settings;
using Coder.Presentation.Workbench;

namespace Coder.App;

/// <summary>
/// The one place where concrete implementations are chosen and wired together. The object graph is
/// small, so plain constructor injection keeps it explicit without a container.
/// </summary>
public static class CompositionRoot
{
    /// <summary>One client for the marketplace: requests name only the app, never the user.</summary>
    private static readonly HttpClient Http = CreateHttp();

    public static MainWindow CreateMainWindow(IReadOnlyList<string> args, ISettingsStore settings)
    {
        MainWindow? window = null;
        var dialogs = new DialogService(() => window!);
        var dataFolder = settings is JsonSettingsStore store
            ? Path.GetDirectoryName(store.FilePath)!
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Coder");

        // VS Code's themes, themes from extensions installed in Coder (they win), and loose theme files in "themes".
        var extensions = new ExtensionStore(Path.Combine(dataFolder, "extensions"), Http);
        var themesFolder = Path.Combine(dataFolder, "themes");
        var themes = new ThemeCatalog([.. VsCodeThemeCatalog.DefaultExtensionFolders(), extensions.Folder], themesFolder);
        var userSettings = settings.Load();
        List<IExtensionSource> sources = []; // in this order in the results: Coder's own registry first
        if (userSettings.ExtensionRegistry is { Length: > 0 } registry)
        {
            sources.Add(new CoderRegistrySource(Http, registry));
        }

        if (userSettings.OpenVsx)
        {
            sources.Add(new OpenVsxSource(Http));
        }

        // Language servers and tools Coder ships: bundled next to the app, or installed on first use into "servers" next to settings.json.
        var managed = new ManagedServers([Path.Combine(AppContext.BaseDirectory, "servers")], Path.Combine(dataFolder, "servers"));
        var workbench = new WorkbenchViewModel(new LocalFileSystem(), dialogs, settings, new ProcessLanguageServerLauncher(managed), themes, managed, extensions, sources, new VelopackUpdateService());
        ThemeManager.Follow(workbench, themes); // before the window is built, so it opens in the right colors
        window = new MainWindow(workbench);
        CrashReporter.Install(message => workbench.StatusBar.Message = message);
        workbench.Updates.StartBackgroundChecks(); // quietly, after start-up; offers a restart when a new version is ready

        if (args.Count > 0)
        {
            OpenFromCommandLine(workbench, args);
        }
        else
        {
            workbench.RestoreLastFolder();
        }

        return window;
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Coder/0.1");
        return http;
    }

    /// <summary><c>coder &lt;folder&gt;</c> opens a folder, <c>coder &lt;file&gt;</c> opens a file.</summary>
    private static void OpenFromCommandLine(WorkbenchViewModel workbench, IReadOnlyList<string> args)
    {
        foreach (var arg in args)
        {
            var path = Path.GetFullPath(arg);
            if (Directory.Exists(path))
            {
                workbench.OpenFolder(path);
            }
            else if (File.Exists(path))
            {
                _ = workbench.OpenFileAsync(path);
            }
        }
    }
}
