using System.Diagnostics;

namespace Coder.Core.Lsp;

/// <summary>
/// A tool Coder installs and runs itself with Node.js: npm packages at pinned versions, and the script
/// (inside node_modules) to run.
/// </summary>
/// <param name="Packages">npm packages as <c>name@version</c>; the first one is the tool itself.</param>
/// <param name="EntryScript">The script to run, relative to node_modules (<c>prettier/bin/prettier.cjs</c>).</param>
public sealed record NpmTool(string DisplayName, IReadOnlyList<string> Packages, string EntryScript);

/// <summary>
/// Language servers and tools that come with Coder, like Zed's: HTML, CSS, JSON, TypeScript and
/// JavaScript, Tailwind CSS and Prettier. A release can bundle them (a <c>servers</c> folder next to the
/// app, made by <c>tools/bundle-servers.ps1</c>, with its own Node.js); otherwise Coder installs each one
/// with npm the first time it is needed, into its own data folder, so nothing is set up by hand.
/// </summary>
public sealed class ManagedServers(IReadOnlyList<string> bundledFolders, string installFolder)
{
    // npm installs one at a time: two into the same folder at once corrupt it.
    private static readonly SemaphoreSlim Installing = new(1, 1);

    /// <summary>Where Coder installs tools on demand (<c>&lt;user data&gt;/servers</c>).</summary>
    public string InstallFolder => installFolder;

    /// <summary>The tool's script when it is bundled or installed, else null.</summary>
    public string? Find(NpmTool tool) => Folders()
        .Select(folder => Path.Combine(folder, "node_modules", tool.EntryScript))
        .FirstOrDefault(File.Exists);

    /// <summary>Node.js: bundled with the app (<c>servers/node/node.exe</c>), else the one on PATH.</summary>
    public string? Node() => bundledFolders
        .Select(folder => Path.Combine(folder, "node", OperatingSystem.IsWindows() ? "node.exe" : "node"))
        .FirstOrDefault(File.Exists) ?? ExecutableLocator.Find("node");

    /// <summary>Whether the tool can be installed here (npm is available).</summary>
    public bool CanInstall => ExecutableLocator.Find("npm") is not null && Node() is not null;

    /// <summary>
    /// Installs the tool's packages into <see cref="InstallFolder"/> with npm (one install at a time).
    /// Returns npm's error output on failure, or null when the tool is ready.
    /// </summary>
    public async Task<string?> InstallAsync(NpmTool tool, CancellationToken cancellationToken = default)
    {
        await Installing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Find(tool) is not null)
            {
                return null; // installed meanwhile (by an earlier request for the same packages)
            }

            if (ExecutableLocator.Find("npm") is not { } npm)
            {
                return "npm was not found. Install Node.js (https://nodejs.org) to get this feature.";
            }

            Directory.CreateDirectory(installFolder);
            var start = new ProcessStartInfo(npm)
            {
                WorkingDirectory = installFolder,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in (string[])["install", "--prefix", installFolder, "--no-audit", "--no-fund", "--loglevel=error", "--save-exact", .. tool.Packages])
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start) ?? throw new IOException("Could not start npm.");
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await output.ConfigureAwait(false);
            var message = (await error.ConfigureAwait(false)).Trim();
            return process.ExitCode == 0 && Find(tool) is not null
                ? null
                : message.Length > 0 ? message : $"npm install failed with exit code {process.ExitCode}.";
        }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception)
        {
            return exception.Message;
        }
        finally
        {
            Installing.Release();
        }
    }

    private IEnumerable<string> Folders() => bundledFolders.Append(installFolder);
}

/// <summary>The tools Coder ships (pinned versions; the TypeScript server's needs Node.js 20 or newer).</summary>
public static class ManagedTools
{
    private static readonly string[] WebServers = ["vscode-langservers-extracted@4.10.0"];

    public static NpmTool HtmlServer { get; } = new("HTML language server", WebServers, "vscode-langservers-extracted/bin/vscode-html-language-server");

    public static NpmTool CssServer { get; } = new("CSS language server", WebServers, "vscode-langservers-extracted/bin/vscode-css-language-server");

    public static NpmTool JsonServer { get; } = new("JSON language server", WebServers, "vscode-langservers-extracted/bin/vscode-json-language-server");

    public static NpmTool TypeScriptServer { get; } = new("TypeScript language server", ["typescript-language-server@5.3.0", "typescript@5.9.3"], "typescript-language-server/lib/cli.mjs");

    public static NpmTool TailwindServer { get; } = new("Tailwind CSS IntelliSense", ["@tailwindcss/language-server@0.16.0"], "@tailwindcss/language-server/bin/tailwindcss-language-server");

    public static NpmTool Prettier { get; } = new("Prettier", ["prettier@3.10.1"], "prettier/bin/prettier.cjs");

    public static IReadOnlyList<NpmTool> All { get; } = [HtmlServer, CssServer, JsonServer, TypeScriptServer, TailwindServer, Prettier];
}
