using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Coder.Core.Extensions;
using Coder.Core.Themes;

namespace Coder.Core.Tests;

public sealed class ExtensionStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("coder-extensions-").FullName;
    private readonly HttpClient _http = new();

    public void Dispose()
    {
        _http.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private string ExtensionsFolder => Path.Combine(_root, "extensions");

    /// <summary>A registry on disk: install a theme from it, and the theme catalog finds it like a VS Code extension's.</summary>
    [Fact]
    public async Task Installs_a_theme_from_a_registry_and_the_catalog_finds_it()
    {
        var package = Vsix("acme", "midnight", "1.0.0", "Acme Midnight");
        var index = Index(("acme", "midnight", "1.0.0", package, Sha(package)));
        var source = new CoderRegistrySource(_http, index);
        var listing = Assert.Single(await source.SearchAsync("midnight", TestContext.Current.CancellationToken));
        Assert.Equal("acme.midnight", listing.Id);

        var store = new ExtensionStore(ExtensionsFolder, _http);
        var changed = 0;
        store.Changed += (_, _) => changed++;
        var installed = await store.InstallAsync(listing, TestContext.Current.CancellationToken);

        Assert.Equal(["Acme Midnight"], installed.Themes);
        Assert.Equal(1, changed);
        Assert.Single(store.Installed());
        var theme = Assert.Single(VsCodeThemeCatalog.Discover([ExtensionsFolder], null));
        Assert.Equal("vscode:acme.midnight/Acme Midnight", theme.Id);
        Assert.True(File.Exists(theme.Path));
    }

    /// <summary>A download that doesn't match the published checksum is refused, and nothing is left behind.</summary>
    [Fact]
    public async Task A_package_with_the_wrong_checksum_is_refused()
    {
        var package = Vsix("acme", "midnight", "1.0.0", "Acme Midnight");
        var listing = Assert.Single(await new CoderRegistrySource(_http, Index(("acme", "midnight", "1.0.0", package, new string('0', 64))))
            .SearchAsync(string.Empty, TestContext.Current.CancellationToken));

        var store = new ExtensionStore(ExtensionsFolder, _http);
        var error = await Assert.ThrowsAsync<ExtensionException>(() => store.InstallAsync(listing, TestContext.Current.CancellationToken));
        Assert.Contains("checksum", error.Message, StringComparison.Ordinal);
        Assert.Empty(store.Installed());
    }

    /// <summary>A package whose entries try to escape its folder ("../") is refused before anything is written outside.</summary>
    [Fact]
    public void A_package_that_writes_outside_its_folder_is_refused()
    {
        var package = Path.Combine(_root, "evil.zip");
        using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            Write(zip, "extension/package.json", Manifest("acme", "evil", "1.0.0", "Evil"));
            Write(zip, "extension/../../escaped.txt", "gotcha");
        }

        var store = new ExtensionStore(ExtensionsFolder, _http);
        var listing = Listing("acme.evil", package);
        Assert.Throws<ExtensionException>(() => store.Install(package, listing));
        Assert.False(File.Exists(Path.Combine(_root, "escaped.txt")));
        Assert.Empty(store.Installed());
    }

    /// <summary>Installing a newer version replaces the older one; uninstalling removes it.</summary>
    [Fact]
    public void A_new_version_replaces_the_old_one_and_uninstall_removes_it()
    {
        var store = new ExtensionStore(ExtensionsFolder, _http);
        store.Install(Vsix("acme", "midnight", "1.0.0", "Acme Midnight"), Listing("acme.midnight", "v1"));
        store.Install(Vsix("acme", "midnight", "1.2.0", "Acme Midnight"), Listing("acme.midnight", "v2"));

        var installed = Assert.Single(store.Installed());
        Assert.Equal("1.2.0", installed.Version);
        Assert.Single(Directory.GetDirectories(ExtensionsFolder));

        store.Uninstall("acme.midnight");
        Assert.Empty(store.Installed());
    }

    /// <summary>The package must be the extension that was asked for.</summary>
    [Fact]
    public void A_package_for_a_different_extension_is_refused()
    {
        var store = new ExtensionStore(ExtensionsFolder, _http);
        var error = Assert.Throws<ExtensionException>(() => store.Install(Vsix("other", "thing", "1.0.0", "Thing"), Listing("acme.midnight", "x")));
        Assert.Contains("other.thing", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Versions_compare_numerically()
    {
        Assert.True(ExtensionStore.CompareVersions("1.10.0", "1.9.2") > 0);
        Assert.True(ExtensionStore.CompareVersions("2.0.0-beta", "1.9.9") > 0);
    }

    private static ExtensionListing Listing(string id, string download) =>
        new(id, id, id.Split('.')[0], string.Empty, "1.0.0", 0, false, null, download, null, null, "Test");

    /// <summary>A .vsix as Open VSX serves it: the extension under extension/, with one dark theme.</summary>
    private string Vsix(string publisher, string name, string version, string themeLabel)
    {
        var path = Path.Combine(_root, $"{publisher}.{name}-{version}.vsix");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Write(zip, "extension.vsixmanifest", "<PackageManifest />");
        Write(zip, "extension/package.json", Manifest(publisher, name, version, themeLabel));
        Write(zip, "extension/themes/theme.json", """{ "name": "x", "type": "dark", "colors": { "editor.background": "#101010" }, "tokenColors": [] }""");
        return path;
    }

    private static string Manifest(string publisher, string name, string version, string themeLabel) =>
        $$"""
        { "publisher": "{{publisher}}", "name": "{{name}}", "version": "{{version}}", "displayName": "{{themeLabel}}",
          "contributes": { "themes": [ { "label": "{{themeLabel}}", "uiTheme": "vs-dark", "path": "./themes/theme.json" } ] } }
        """;

    private string Index(params (string Publisher, string Name, string Version, string Download, string Sha)[] entries)
    {
        var path = Path.Combine(_root, "index.json");
        var items = entries.Select(e => $$"""{ "publisher": "{{e.Publisher}}", "name": "{{e.Name}}", "version": "{{e.Version}}", "download": "{{Path.GetFileName(e.Download)}}", "sha256": "{{e.Sha}}", "description": "A calm dark theme" }""");
        File.WriteAllText(path, $$"""{ "extensions": [ {{string.Join(", ", items)}} ] }""");
        return path;
    }

    private static string Sha(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));

    private static void Write(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
