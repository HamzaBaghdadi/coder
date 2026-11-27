using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Coder.App.Controls;
using Coder.App.Workbench;
using Coder.Core.Extensions;
using Coder.Core.Files;
using Coder.Core.Sync;
using Coder.Presentation.Settings;
using Coder.Presentation.Workbench;

namespace Coder.App.Tests;

public sealed partial class WorkbenchUiTests
{
    /// <summary>
    /// The cog's account items follow Settings Sync: "Sign in with GitHub…", then (signed in through the
    /// device flow, the code copied and GitHub opened) the account with Sync Now / Turn Off / Sign Out.
    /// Signing in pulls: the synced font size is applied, the synced extension installed, and the status
    /// bar says what replaced the local values. A local change is pushed a moment later.
    /// </summary>
    [AvaloniaFact]
    public async Task Settings_sync_menu_states_sign_in_pull_and_push()
    {
        var root = Directory.CreateTempSubdirectory("coder-sync-ui-").FullName;
        using var http = new HttpClient();
        using var github = new FakeGitHubForUi();
        using var githubHttp = new HttpClient(github, disposeHandler: false);
        try
        {
            // Another machine already synced: font size 20 and the Acme Ember theme from Coder's registry.
            var package = ThemePackage(root, "acme", "ember", "Acme Ember");
            var sha = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(package, TestContext.Current.CancellationToken)));
            var index = Path.Combine(root, "index.json");
            await File.WriteAllTextAsync(index, $$"""{ "extensions": [ { "publisher": "acme", "name": "ember", "displayName": "Acme Ember", "version": "1.0.0", "download": "{{Path.GetFileName(package)}}", "sha256": "{{sha}}" } ] }""", TestContext.Current.CancellationToken);
            github.Gist[SettingsSyncService.MarkerFile] = "# Coder Settings Sync";
            github.Gist[SettingsSyncService.SettingsFile] = """{ "version": 1, "settings": { "editorFontSize": 20 }, "modified": { "editorFontSize": "2026-01-01T00:00:00+00:00" } }""";
            github.Gist[SettingsSyncService.ExtensionsFile] = """{ "version": 1, "extensions": [ { "id": "acme.ember", "version": "1.0.0", "source": "Coder", "modified": "2026-01-01T00:00:00+00:00" } ] }""";

            var store = new ExtensionStore(Path.Combine(root, "installed"), http);
            var workbench = new WorkbenchViewModel(new LocalFileSystem(), new NoDialogs(), new MemorySettingsStore(),
                extensionStore: store, extensionSources: [new CoderRegistrySource(http, index)]);
            var opened = new List<Uri>();
            var copied = new List<string>();
            var service = new SettingsSyncService(githubHttp, "client-123", Path.Combine(root, "sync"), new MemoryTokenStore(), delay: (_, _) => Task.CompletedTask);
            var sync = workbench.EnableSettingsSync(service, opened.Add, text =>
            {
                copied.Add(text);
                return Task.CompletedTask;
            }, TimeSpan.FromMilliseconds(20));
            var window = new MainWindow(workbench) { Width = 1280, Height = 800 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var account = window.GetVisualDescendants().OfType<AccountButton>().Single();
            Assert.IsType<Icon>(account.Face); // the person icon while signed out
            Assert.Contains("Sign in with GitHub to Sync Settings", PanelNames(account));
            Assert.DoesNotContain("Sign Out", PanelNames(account));
            Assert.DoesNotContain(MenuHeaders(workbench), h => h.Contains("GitHub", StringComparison.Ordinal)); // the account lives in its own button now
            account.Toggle();
            Assert.True(account.IsOpen);
            Save(window, "account-signed-out");
            account.Toggle();

            await workbench.Commands.ExecuteAsync(Presentation.Commands.CommandIds.SettingsSyncSignIn);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(["ABCD-1234"], copied);
            Assert.Equal([new Uri("https://github.com/login/device")], opened);
            Assert.Equal(["Sync Now", "Turn Off Sync", "Sign Out"], PanelNames(account).Where(n => n is "Sync Now" or "Turn Off Sync" or "Turn On Sync" or "Sign Out"));
            Assert.Contains("The Octocat", PanelTexts(account));
            Assert.Contains("octocat · GitHub", PanelTexts(account));
            Assert.Contains("Settings Sync is on", PanelTexts(account));

            // The title bar shows the account's picture, round.
            var face = Assert.IsType<Border>(account.Face);
            Assert.IsType<ImageBrush>(face.Background);
            Assert.Equal(face.Width / 2, face.CornerRadius.TopLeft);
            account.Toggle();
            Save(window, "account-signed-in");
            account.Toggle();

            // Pulled: the synced value replaced the default here (and says so), the extension is installed.
            Assert.Equal(20, workbench.EditorFontSize);
            Assert.Contains("replacing the ones here (editorFontSize)", workbench.StatusBar.Message, StringComparison.Ordinal);
            Assert.Contains("Installed Acme Ember", workbench.StatusBar.Message, StringComparison.Ordinal);
            Assert.Equal("acme.ember", Assert.Single(store.Installed()).Id);
            Assert.StartsWith("Signed in as octocat. Last synced", sync.StatusText, StringComparison.Ordinal);

            // The settings page has the row, with the next thing to do.
            workbench.OpenSettingsPage();
            var row = Assert.Single(((SettingsPageViewModel)workbench.Editors.ActiveTab!).Items.OfType<ActionSetting>(), s => s.Category == "Settings Sync");
            Assert.Equal("Sync Now", row.ActionLabel);
            Save(window, "settings-sync-signed-in");

            // A change here is pushed a moment later, without the window's own keys.
            workbench.EditorFontSize = 24;
            await WaitUntilAsync(() => github.Gist[SettingsSyncService.SettingsFile].Contains("\"editorFontSize\": 24", StringComparison.Ordinal));
            var pushed = JsonNode.Parse(github.Gist[SettingsSyncService.SettingsFile])!["settings"]!.AsObject();
            Assert.False(pushed.ContainsKey("window"));
            Assert.False(pushed.ContainsKey("recentFolders"));
            Assert.False(pushed.ContainsKey("gpuAcceleration"));
            Assert.True(pushed.ContainsKey("colorTheme"));

            await WaitUntilAsync(() => !sync.IsSyncing); // the push after the extension install, too
            await workbench.Commands.ExecuteAsync(Presentation.Commands.CommandIds.SettingsSyncTurnOff);
            Assert.Contains("Turn On Sync", PanelNames(account));
            Assert.DoesNotContain("Sync Now", PanelNames(account));
            Assert.Contains("Settings Sync is off", PanelTexts(account));
            Assert.Equal("Turn On Sync", row.ActionLabel);

            await workbench.Commands.ExecuteAsync(Presentation.Commands.CommandIds.SettingsSyncSignOut);
            Assert.Contains("Sign in with GitHub to Sync Settings", PanelNames(account));
            Assert.IsType<Icon>(account.Face);
            Assert.False(sync.IsSignedIn);
            Assert.Equal("Sign in with GitHub…", row.ActionLabel);
            sync.Dispose();
            service.Dispose();
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Without Settings Sync (no GitHub app) there is no account button and no account items.</summary>
    [AvaloniaFact]
    public void No_account_button_without_settings_sync()
    {
        var (window, workbench) = CreateWindow();
        window.Show();
        Assert.Empty(window.GetVisualDescendants().OfType<AccountButton>());
        Assert.DoesNotContain(MenuHeaders(workbench), h => h.Contains("GitHub", StringComparison.Ordinal) || h == "Sign Out");
        Assert.Contains("Check for Updates…", MenuHeaders(workbench));
        window.Close();
    }

    /// <summary>What Settings Sync sends leaves out the machine's own keys; what it applies keeps them.</summary>
    [Fact]
    public void Synced_settings_leave_out_machine_specific_keys()
    {
        var local = new UserSettings
        {
            RecentFolders = [@"C:\work"],
            Window = new WindowPlacement(10, 10, 800, 600, false),
            GpuAcceleration = false,
            EditorFontSize = 15,
        };
        var json = SettingsSyncViewModel.ToJson(local);
        foreach (var key in SettingsSyncViewModel.MachineKeys)
        {
            Assert.False(json.ContainsKey(key), key);
        }

        Assert.Equal(15, json["editorFontSize"]!.GetValue<double>());
        var synced = new JsonObject { ["editorFontSize"] = 21, ["gpuAcceleration"] = true, ["recentFolders"] = new JsonArray("D:\\elsewhere"), ["futureSetting"] = 1 };
        var applied = SettingsSyncViewModel.FromJson(synced, local)!;
        Assert.Equal(21, applied.EditorFontSize);
        Assert.False(applied.GpuAcceleration);
        Assert.Equal([@"C:\work"], applied.RecentFolders);
        Assert.Equal(local.Window, applied.Window);
    }

    private static List<string> PanelNames(AccountButton account) =>
        account.BuildPanel().GetLogicalDescendants().OfType<Button>().Select(b => Avalonia.Automation.AutomationProperties.GetName(b) ?? string.Empty).ToList();

    private static List<string> PanelTexts(AccountButton account) =>
        account.BuildPanel().GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty).ToList();

    private static List<string> MenuHeaders(WorkbenchViewModel workbench) =>
        TitleBar.ManageMenuItems(workbench).OfType<MenuItem>().Select(i => i.Header as string ?? string.Empty).ToList();

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }

    /// <summary>github.com's device flow and one gist, in memory.</summary>
    private sealed class FakeGitHubForUi : HttpMessageHandler
    {
        public Dictionary<string, string> Gist { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.RequestUri.Host == "avatars.githubusercontent.com")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(AvatarPng()) };
            }

            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var json = path switch
            {
                "/login/device/code" => """{"device_code":"dev","user_code":"ABCD-1234","verification_uri":"https://github.com/login/device","expires_in":900,"interval":5}""",
                "/login/oauth/access_token" => """{"access_token":"gho_ui"}""",
                "/user" => """{"login":"octocat","name":"The Octocat","avatar_url":"https://avatars.githubusercontent.com/u/1?v=4"}""",
                "/gists" when request.Method == HttpMethod.Get => $"[{GistJson()}]",
                _ => null,
            };
            if (json is null && path == "/gists/g1")
            {
                if (request.Method == HttpMethod.Patch)
                {
                    foreach (var (name, file) in JsonNode.Parse(body)!["files"]!.AsObject())
                    {
                        Gist[name] = file!["content"]!.GetValue<string>();
                    }
                }

                json = GistJson();
            }

            return json is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }

        /// <summary>A real (tiny, orange) PNG, as GitHub's avatar server would send.</summary>
        private static byte[] AvatarPng()
        {
            using var bitmap = new RenderTargetBitmap(new Avalonia.PixelSize(16, 16));
            using (var context = bitmap.CreateDrawingContext())
            {
                context.FillRectangle(Brushes.Orange, new Avalonia.Rect(0, 0, 16, 16));
            }

            using var stream = new MemoryStream();
            bitmap.Save(stream);
            return stream.ToArray();
        }

        private string GistJson() => new JsonObject
        {
            ["id"] = "g1",
            ["files"] = new JsonObject(Gist.Select(f => KeyValuePair.Create<string, JsonNode?>(f.Key, new JsonObject { ["content"] = f.Value }))),
        }.ToJsonString();
    }
}
