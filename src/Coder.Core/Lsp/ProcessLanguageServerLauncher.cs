namespace Coder.Core.Lsp;

/// <summary>Starts language servers. Abstracted so tests (and future remote servers) can supply their own.</summary>
public interface ILanguageServerLauncher
{
    /// <summary>True when the server can be started on this machine.</summary>
    bool IsInstalled(LanguageServerDefinition server);

    /// <summary>Starts the server for a project root. Throws when it cannot be started.</summary>
    LanguageClient Launch(LanguageServerDefinition server, string rootPath);

    /// <summary>True when Coder can install a missing server itself.</summary>
    bool CanInstall(LanguageServerDefinition server) => false;

    /// <summary>Installs a missing server; returns why it failed, or null when it is ready.</summary>
    Task<string?> InstallAsync(LanguageServerDefinition server, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>($"{server.Name} can't be installed automatically: {server.InstallHint}");
}

/// <summary>
/// Runs each language server as a child process talking over stdin/stdout: the copy Coder ships (run
/// with Node.js, see <see cref="ManagedServers"/>) when there is one, else the command found on PATH.
/// </summary>
public sealed class ProcessLanguageServerLauncher(ManagedServers? managed = null) : ILanguageServerLauncher
{
    public bool IsInstalled(LanguageServerDefinition server) => Managed(server) is not null || ExecutableLocator.Find(server.Command) is not null;

    public bool CanInstall(LanguageServerDefinition server) => server.Tool is not null && managed is { CanInstall: true };

    public Task<string?> InstallAsync(LanguageServerDefinition server, CancellationToken cancellationToken = default) =>
        server.Tool is { } tool && managed is not null
            ? managed.InstallAsync(tool, cancellationToken)
            : Task.FromResult<string?>($"{server.Name} can't be installed automatically: {server.InstallHint}");

    public LanguageClient Launch(LanguageServerDefinition server, string rootPath)
    {
        // Coder's own copy, run with Node.js; else the command on PATH.
        var (executable, prefix) = Managed(server) is var (node, script)
            ? (node, (string[])[script])
            : (ExecutableLocator.Find(server.Command) ?? throw new FileNotFoundException($"{server.Command} was not found on PATH."), []);
        return new LanguageClient(ProcessTransport.Start(executable, prefix.Concat(server.Arguments), rootPath), rootPath, server.Name)
        {
            Settings = server.Settings,
            InitializationOptions = server.InitializationOptions,
        };
    }

    /// <summary>Node.js and the script of Coder's copy of the server, when both are here.</summary>
    private (string Node, string Script)? Managed(LanguageServerDefinition server) =>
        server.Tool is { } tool && managed?.Find(tool) is { } script && managed.Node() is { } node ? (node, script) : null;
}

/// <summary>Finds a command the way a shell would: on PATH (with Windows executable extensions) and in common tool folders.</summary>
public static class ExecutableLocator
{
    public static string? Find(string command)
    {
        if (Path.IsPathRooted(command))
        {
            return File.Exists(command) ? command : null;
        }

        var extensions = OperatingSystem.IsWindows() && !Path.HasExtension(command)
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];

        foreach (var folder in SearchFolders())
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(folder, command + extension.ToLowerInvariant());
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>PATH, then the folders where Go, Cargo, .NET and npm install tools (an app started from the desktop may have a shorter PATH).</summary>
    private static IEnumerable<string> SearchFolders()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var path = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        string[] toolFolders =
        [
            Path.Combine(home, "go", "bin"),
            Path.Combine(home, ".cargo", "bin"),
            Path.Combine(home, ".dotnet", "tools"),
            Path.Combine(appData, "npm"),
            Path.Combine(home, ".local", "bin"),
            "/usr/local/bin",
            "/opt/homebrew/bin",
        ];
        return path.Select(p => p.Trim('"')).Concat(toolFolders).Where(p => p.Length > 0).Distinct(DocumentUri.PathComparer);
    }
}
