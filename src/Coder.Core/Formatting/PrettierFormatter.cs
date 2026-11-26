using System.Diagnostics;
using System.Text;
using Coder.Core.Lsp;

namespace Coder.Core.Formatting;

/// <summary>
/// Formats with Prettier (shipped with Coder, see <see cref="ManagedServers"/>), the formatter most web
/// projects use. Prettier finds the project's own settings (.prettierrc, "prettier" in package.json,
/// .editorconfig) from the file's path, so the result matches the project's.
/// </summary>
public sealed class PrettierFormatter(ManagedServers managed)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>The file types Prettier formats.</summary>
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".htm", ".vue", ".css", ".scss", ".less",
        ".js", ".mjs", ".cjs", ".jsx", ".ts", ".mts", ".cts", ".tsx",
        ".json", ".jsonc", ".md", ".markdown", ".mdx", ".yaml", ".yml", ".graphql", ".gql", ".hbs", ".handlebars",
    };

    public static bool Handles(string? path) => path is not null && Extensions.Contains(Path.GetExtension(path));

    /// <summary>Whether Prettier is here or can be installed.</summary>
    public bool IsAvailable => managed.Find(ManagedTools.Prettier) is not null || managed.CanInstall;

    /// <summary>
    /// The formatted text of the file at <paramref name="path"/> (its current text is <paramref name="text"/>,
    /// unsaved edits included), or Prettier's error (a syntax error says where). Installs Prettier on first use.
    /// </summary>
    public async Task<(string? Formatted, string? Error)> FormatAsync(string path, string text, CancellationToken cancellationToken = default)
    {
        if (managed.Find(ManagedTools.Prettier) is null && await managed.InstallAsync(ManagedTools.Prettier, cancellationToken).ConfigureAwait(false) is { } installError)
        {
            return (null, $"Couldn't install Prettier: {installError}");
        }

        if (managed.Find(ManagedTools.Prettier) is not { } script || managed.Node() is not { } node)
        {
            return (null, "Prettier needs Node.js (https://nodejs.org).");
        }

        var start = new ProcessStartInfo(node)
        {
            WorkingDirectory = Path.GetDirectoryName(path) is { Length: > 0 } folder && Directory.Exists(folder) ? folder : Environment.CurrentDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in (string[])[script, "--stdin-filepath", path])
        {
            start.ArgumentList.Add(argument);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        using var process = Process.Start(start) ?? throw new IOException("Could not start Node.js.");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.StandardInput.WriteAsync(text.AsMemory(), timeout.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode == 0
                ? (await output.ConfigureAwait(false), null)
                : (null, (await error.ConfigureAwait(false)).Trim() is { Length: > 0 } message ? message : $"Prettier failed with exit code {process.ExitCode}.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            return (null, "Prettier took too long.");
        }
    }
}
