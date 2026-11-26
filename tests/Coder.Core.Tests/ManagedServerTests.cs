using Coder.Core.Lsp;

namespace Coder.Core.Tests;

public sealed class ManagedServerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("coder-managed-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>The release bundling script installs exactly the packages and versions Coder expects.</summary>
    [Fact]
    public void The_bundling_script_ships_the_same_versions_as_the_app()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryRoot(), "tools", "bundle-servers.ps1"));
        foreach (var package in ManagedTools.All.SelectMany(tool => tool.Packages).Distinct())
        {
            Assert.Contains($"'{package}'", script, StringComparison.Ordinal);
        }
    }

    /// <summary>A bundled copy (next to the app) is used before one installed into the data folder.</summary>
    [Fact]
    public void Bundled_servers_come_first()
    {
        var bundled = Path.Combine(_root, "app", "servers");
        var installed = Path.Combine(_root, "data", "servers");
        var tool = ManagedTools.Prettier;
        var managed = new ManagedServers([bundled], installed);
        Assert.Null(managed.Find(tool));

        foreach (var folder in new[] { installed, bundled })
        {
            var script = Path.Combine(folder, "node_modules", tool.EntryScript);
            Directory.CreateDirectory(Path.GetDirectoryName(script)!);
            File.WriteAllText(script, "// prettier");
            Assert.Equal(script, managed.Find(tool));
        }

        var node = Path.Combine(bundled, "node", OperatingSystem.IsWindows() ? "node.exe" : "node");
        Directory.CreateDirectory(Path.GetDirectoryName(node)!);
        File.WriteAllText(node, string.Empty);
        Assert.Equal(node, managed.Node());
    }

    [Fact]
    public void Web_languages_use_the_shipped_servers()
    {
        Assert.Equal(ManagedTools.TypeScriptServer, LanguageServerCatalog.For("typescript")!.Tool);
        Assert.Equal(ManagedTools.TypeScriptServer, LanguageServerCatalog.For("javascript")!.Tool);
        Assert.Equal(ManagedTools.HtmlServer, LanguageServerCatalog.For("html")!.Tool);
        Assert.Equal(ManagedTools.CssServer, LanguageServerCatalog.For("css")!.Tool);
        Assert.Equal(ManagedTools.JsonServer, LanguageServerCatalog.For("json")!.Tool);
    }

    private static string RepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", ".."));
}
