using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Coder.Core.Extensions;
using Coder.Core.Lsp;

namespace Coder.Core.Tests;

/// <summary>What extension programs need below the UI: manifests, per-platform registry entries, consent, and async JSON-RPC.</summary>
public sealed class ProgramExtensionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("coder-programs-").FullName;
    private readonly HttpClient _http = new();

    public void Dispose()
    {
        _http.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private string Package(string version, string manifestExtra = "", params (string Name, string Text)[] files)
    {
        var path = Path.Combine(_root, $"acme.helper-{version}-{Guid.NewGuid():N}.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, string text)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(text);
        }

        Add("extension/package.json", $$"""
            { "publisher": "acme", "name": "helper", "version": "{{version}}", "displayName": "Acme Helper"{{manifestExtra}} }
            """);
        foreach (var (name, text) in files)
        {
            Add("extension/" + name, text);
        }

        return path;
    }

    private static string Sha(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));

    private static ExtensionListing Listing(string package) =>
        new("acme.helper", "Acme Helper", "acme", "", "1.0.0", 0, true, null, package, Sha(package), null, "Coder") { RunsProgram = true };

    private const string ProgramManifest = $$"""
        ,
        "coder": { "program": { "{{"RID"}}": "bin/helper.exe", "elsewhere-x64": "bin/other" } },
        "contributes": {
          "commands": [
            { "command": "helper.open", "title": "Open", "category": "Helper" },
            { "command": "helper.plain", "title": "Plain" },
            { "command": "", "title": "no id" }
          ],
          "keybindings": [ { "command": "helper.open", "key": "ctrl+shift+escape" }, { "command": "helper.plain", "key": "ctrl+k ctrl+h" } ]
        }
        """;

    [Fact]
    public async Task A_program_extension_reads_its_program_commands_and_package_checksum()
    {
        var store = new ExtensionStore(Path.Combine(_root, "installed"), _http);
        var package = Package("1.0.0", ProgramManifest.Replace("RID", Platform.RuntimeId, StringComparison.Ordinal), ("bin/helper.exe", "not really"), ("bin/other", "x"));

        var installed = await store.InstallAsync(Listing(package), TestContext.Current.CancellationToken);

        Assert.True(installed.DeclaresProgram);
        Assert.Equal(Path.Combine(installed.Folder, "bin", "helper.exe"), installed.ProgramPath);
        Assert.Equal(Sha(package), installed.PackageSha256);
        Assert.Equal(
            [new ExtensionCommand("helper.open", "Helper", "Open", "Ctrl+Shift+Escape"), new ExtensionCommand("helper.plain", "Acme Helper", "Plain", "Ctrl+K Ctrl+H")],
            installed.Commands);
        Assert.Equal(installed.ProgramPath, store.Find("acme.helper")?.ProgramPath);
    }

    [Fact]
    public void A_program_outside_the_package_or_for_another_platform_is_not_run()
    {
        var store = new ExtensionStore(Path.Combine(_root, "installed"), _http);
        var outside = Package("1.0.0", $$""", "coder": { "program": { "{{Platform.RuntimeId}}": "../../evil.exe" } }""");
        Assert.Null(store.Install(outside, Listing(outside)).ProgramPath);

        var otherPlatform = Package("1.0.1", """, "coder": { "program": { "elsewhere-x64": "bin/other" } }""", ("bin/other", "x"));
        var installed = store.Install(otherPlatform, Listing(otherPlatform));
        Assert.True(installed.DeclaresProgram);
        Assert.Null(installed.ProgramPath);
    }

    [Fact]
    public async Task Updating_or_uninstalling_stops_the_program_first()
    {
        var store = new ExtensionStore(Path.Combine(_root, "installed"), _http);
        var stopped = new List<string>();
        store.BeforeRemoveAsync = id =>
        {
            stopped.Add(id);
            return Task.CompletedTask;
        };

        var first = Package("1.0.0");
        await store.InstallAsync(Listing(first), TestContext.Current.CancellationToken);
        Assert.Empty(stopped); // nothing ran yet

        var second = Package("1.1.0");
        await store.InstallAsync(Listing(second), TestContext.Current.CancellationToken);
        Assert.Equal(["acme.helper"], stopped);

        await store.UninstallAsync("acme.helper");
        Assert.Equal(["acme.helper", "acme.helper"], stopped);
        Assert.Empty(store.Installed());
    }

    [Theory]
    [InlineData("ctrl+escape", "Ctrl+Escape")]
    [InlineData("ctrl+shift+k", "Ctrl+Shift+K")]
    [InlineData("ctrl+k ctrl+t", "Ctrl+K Ctrl+T")]
    [InlineData("cmd+alt+f1", "Meta+Alt+F1")]
    public void Vs_code_keys_become_coder_gestures(string key, string gesture) => Assert.Equal(gesture, ExtensionStore.Gesture(key));

    [Fact]
    public async Task The_registry_offers_the_package_built_for_this_machine_only()
    {
        var index = Path.Combine(_root, "index.json");
        await File.WriteAllTextAsync(index, $$"""
            { "extensions": [
              { "publisher": "acme", "name": "helper", "version": "1.0.0", "program": true, "verified": true,
                "platforms": { "{{Platform.RuntimeId}}": { "download": "here.zip", "sha256": "AA" }, "elsewhere-x64": { "download": "there.zip", "sha256": "BB" } } },
              { "publisher": "acme", "name": "elsewhere", "version": "1.0.0", "program": true,
                "platforms": { "elsewhere-x64": { "download": "there.zip", "sha256": "BB" } } },
              { "publisher": "acme", "name": "theme", "version": "1.0.0", "download": "theme.zip", "sha256": "CC" }
            ] }
            """, TestContext.Current.CancellationToken);

        var listings = await new CoderRegistrySource(_http, index).SearchAsync("", TestContext.Current.CancellationToken);

        Assert.Equal(["acme.helper", "acme.theme"], listings.Select(l => l.Id).Order());
        var helper = listings.Single(l => l.Id == "acme.helper");
        Assert.True(helper.RunsProgram);
        Assert.Equal(Path.Combine(_root, "here.zip"), helper.DownloadUrl);
        Assert.Equal("AA", helper.Sha256);
        Assert.False(listings.Single(l => l.Id == "acme.theme").RunsProgram);
    }

    [Fact]
    public void Consent_is_per_package_and_survives_a_restart()
    {
        var file = Path.Combine(_root, "installed", ".trust.json");
        var v1 = new InstalledExtension("acme.helper", "Acme Helper", "acme", "", "1.0.0", _root, []) { PackageSha256 = "AAA" };
        var v2 = v1 with { Version = "1.1.0", PackageSha256 = "BBB" };

        var trust = new ExtensionTrust(file);
        Assert.Equal(ExtensionTrust.Decision.Unknown, trust.For(v1));
        trust.Set(v1, allowed: true);
        Assert.Equal(ExtensionTrust.Decision.Allowed, new ExtensionTrust(file).For(v1));
        Assert.Equal(ExtensionTrust.Decision.Unknown, trust.For(v2)); // an update asks again

        trust.Set(v2, allowed: false);
        Assert.Equal(ExtensionTrust.Decision.Denied, trust.For(v2));
        trust.Forget("acme.helper");
        Assert.Equal(ExtensionTrust.Decision.Unknown, trust.For(v2));

        File.WriteAllText(file, "{ broken");
        Assert.Equal(ExtensionTrust.Decision.Unknown, new ExtensionTrust(file).For(v1)); // unreadable: ask, never run unasked
    }

    [Fact]
    public async Task Async_requests_run_beside_others_and_can_be_cancelled()
    {
        var toServer = new MemoryPipe();
        var toClient = new MemoryPipe();
        using var server = new JsonRpcConnection(toServer, toClient);
        using var client = new JsonRpcConnection(toClient, toServer);
        var waiting = new TaskCompletionSource();
        var cancelled = new TaskCompletionSource();
        server.OnRequestAsync("slow", async (_, token) =>
        {
            waiting.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                cancelled.SetResult();
                throw;
            }

            return null;
        });
        server.OnRequestAsync("echo", (p, _) => Task.FromResult<JsonNode?>(new JsonObject { ["said"] = p.GetProperty("text").GetString() }));
        server.Start();
        client.Start();

        using var stop = new CancellationTokenSource();
        var slow = client.RequestAsync("slow", null, stop.Token);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var echo = await client.RequestAsync("echo", new JsonObject { ["text"] = "hi" }, TestContext.Current.CancellationToken);
        Assert.Equal("hi", echo.GetProperty("said").GetString()); // not stuck behind "slow"

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slow);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); // $/cancelRequest reached the handler
    }

    [Fact]
    public void The_platform_has_a_runtime_id() =>
        Assert.Matches("^(win|linux|osx)-(x64|arm64|x86)$", Platform.RuntimeId);
}
