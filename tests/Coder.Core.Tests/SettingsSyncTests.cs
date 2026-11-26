using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Coder.Core.Sync;

namespace Coder.Core.Tests;

public sealed class SettingsSyncTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coder-sync-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeGitHub _github = new();
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero));

    private const string Avatar = "https://avatars.githubusercontent.com/u/583231?v=4";

    private static readonly byte[] AvatarBytes = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

    public void Dispose()
    {
        _github.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Device_flow_polls_until_the_code_is_entered_and_slows_down_when_asked()
    {
        _github.TokenAnswers.Enqueue("""{"error":"authorization_pending"}""");
        _github.TokenAnswers.Enqueue("""{"error":"slow_down","interval":10}""");
        _github.TokenAnswers.Enqueue("""{"access_token":"gho_secret","token_type":"bearer","scope":"gist"}""");
        var waits = new List<TimeSpan>();
        using var http = new HttpClient(_github);
        var flow = new GitHubDeviceFlow(http, "client-123") { Delay = (wait, _) => { waits.Add(wait); return Task.CompletedTask; } };

        var code = await flow.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal("ABCD-1234", code.UserCode);
        Assert.Equal(new Uri("https://github.com/login/device"), code.VerificationUri);
        Assert.Contains("client_id=client-123", _github.DeviceCodeRequest, StringComparison.Ordinal);
        Assert.Contains("scope=gist", _github.DeviceCodeRequest, StringComparison.Ordinal);

        var token = await flow.WaitForTokenAsync(code, TestContext.Current.CancellationToken);
        Assert.Equal("gho_secret", token);
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)], waits);
        Assert.Equal(new GitHubAccount("octocat", "The Octocat", Avatar), await flow.GetAccountAsync(token, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("expired_token", "expired")]
    [InlineData("access_denied", "cancelled")]
    public async Task Device_flow_says_why_a_sign_in_ended(string error, string expected)
    {
        _github.TokenAnswers.Enqueue($$"""{"error":"{{error}}"}""");
        using var http = new HttpClient(_github);
        var flow = new GitHubDeviceFlow(http, "client-123") { Delay = (_, _) => Task.CompletedTask };
        var code = await flow.StartAsync(TestContext.Current.CancellationToken);

        var failure = await Assert.ThrowsAsync<SyncException>(() => flow.WaitForTokenAsync(code, TestContext.Current.CancellationToken));
        Assert.Contains(expected, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Signing_in_keeps_the_token_out_of_the_state_and_signing_out_forgets_it()
    {
        var tokens = new MemoryTokenStore();
        using var sync = await SignedInAsync("a", tokens);
        Assert.True(sync.IsSignedIn);
        Assert.True(sync.IsEnabled);
        Assert.Equal("octocat", sync.Login);
        Assert.Equal(new GitHubSignIn("octocat", "gho_secret"), tokens.Load());
        Assert.DoesNotContain("gho_secret", File.ReadAllText(sync.StatePath), StringComparison.Ordinal);

        // The account's name and a small copy of its picture, for the title bar.
        Assert.Equal("The Octocat", sync.Name);
        Assert.Equal(AvatarBytes, File.ReadAllBytes(sync.AvatarPath!));
        Assert.Equal(["?v=4&s=64"], _github.AvatarRequests);
        Assert.False(await sync.RefreshAvatarAsync(TestContext.Current.CancellationToken), "already there: not downloaded again");
        File.Delete(sync.AvatarPath!);
        Assert.True(await sync.RefreshAvatarAsync(TestContext.Current.CancellationToken), "missing: downloaded again");

        var avatar = sync.AvatarPath!;
        sync.SignOut();
        Assert.False(sync.IsSignedIn);
        Assert.Null(tokens.Load());
        Assert.False(File.Exists(sync.StatePath));
        Assert.False(File.Exists(avatar));
        Assert.Null(sync.Name);
        await Assert.ThrowsAsync<SyncException>(() => sync.SyncAsync([], [], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task First_machine_creates_a_secret_gist_and_a_second_machine_takes_its_settings()
    {
        using var a = await SignedInAsync("a");
        var first = await a.SyncAsync(Settings(fontSize: 18, theme: "light"), [new("acme.night", "1.0.0", "Open VSX")], TestContext.Current.CancellationToken);
        Assert.True(first.Pushed);
        Assert.Null(first.Settings); // nothing to apply on the machine that had them
        var gist = Assert.Single(_github.Gists.Values);
        Assert.False(gist.Public);
        Assert.Contains(SettingsSyncService.MarkerFile, gist.Files.Keys);
        Assert.Equal(18, JsonNode.Parse(gist.Files[SettingsSyncService.SettingsFile])!["settings"]!["editorFontSize"]!.GetValue<int>());
        Assert.Contains("acme.night", gist.Files[SettingsSyncService.ExtensionsFile], StringComparison.Ordinal);

        // A new machine with defaults: the synced values win (its defaults must not overwrite them), and it says so.
        _time.Advance(TimeSpan.FromHours(1));
        using var b = await SignedInAsync("b");
        var second = await b.SyncAsync(Settings(fontSize: 14, theme: "dark"), [new("other.local", "2.0.0", "Coder")], TestContext.Current.CancellationToken);
        Assert.True(second.IsFirstSync);
        Assert.Equal(18, second.Settings!["editorFontSize"]!.GetValue<int>());
        Assert.Equal("light", second.Settings["colorTheme"]!.GetValue<string>());
        Assert.Equal(["colorTheme", "editorFontSize"], second.Replaced.Order(StringComparer.Ordinal));
        Assert.True(File.Exists(second.Backup), "the local settings were backed up before being replaced");
        Assert.Equal("acme.night", Assert.Single(second.ToInstall).Id);
        Assert.Equal("Open VSX", second.ToInstall[0].Source);
        Assert.True(second.Pushed, "the second machine's own extension joins the list");

        // And the first machine picks up the second one's extension.
        var back = await a.SyncAsync(Settings(fontSize: 18, theme: "light"), [new("acme.night", "1.0.0", "Open VSX")], TestContext.Current.CancellationToken);
        Assert.Null(back.Settings);
        Assert.Equal("other.local", Assert.Single(back.ToInstall).Id);
        Assert.Empty(back.Replaced);
    }

    [Fact]
    public async Task A_change_on_one_machine_reaches_the_other_without_being_reported_as_a_conflict()
    {
        using var a = await SignedInAsync("a");
        using var b = await SignedInAsync("b");
        await a.SyncAsync(Settings(14, "dark"), [], TestContext.Current.CancellationToken);
        await b.SyncAsync(Settings(14, "dark"), [], TestContext.Current.CancellationToken);

        _time.Advance(TimeSpan.FromMinutes(5));
        var pushed = await a.SyncAsync(Settings(20, "dark"), [], TestContext.Current.CancellationToken);
        Assert.True(pushed.Pushed);

        _time.Advance(TimeSpan.FromMinutes(5));
        var pulled = await b.SyncAsync(Settings(14, "dark"), [], TestContext.Current.CancellationToken);
        Assert.Equal(20, pulled.Settings!["editorFontSize"]!.GetValue<int>());
        Assert.Empty(pulled.Replaced);
        Assert.False(pulled.Pushed, "nothing new to push");

        var quiet = await b.SyncAsync(Settings(20, "dark"), [], TestContext.Current.CancellationToken);
        Assert.Null(quiet.Settings);
        Assert.False(quiet.Pushed);
    }

    [Fact]
    public async Task Conflicting_changes_are_last_write_wins_per_key_and_the_loser_is_told()
    {
        using var a = await SignedInAsync("a");
        using var b = await SignedInAsync("b");
        await a.SyncAsync(Settings(14, "dark"), [], TestContext.Current.CancellationToken);
        await b.SyncAsync(Settings(14, "dark"), [], TestContext.Current.CancellationToken);

        // Offline, B changes the font size first; later A changes it too, and the theme. A syncs first.
        _time.Advance(TimeSpan.FromMinutes(1));
        await Assert.ThrowsAsync<SyncException>(() => Offline(() => b.SyncAsync(Settings(16, "dark"), [], TestContext.Current.CancellationToken)));
        _time.Advance(TimeSpan.FromMinutes(1));
        await a.SyncAsync(Settings(22, "light"), [], TestContext.Current.CancellationToken);

        // B comes online with an unrelated change too: A's newer font size wins, B's own minimap change stays and is pushed.
        _time.Advance(TimeSpan.FromMinutes(1));
        var local = Settings(16, "dark");
        local["showMinimap"] = false;
        var result = await b.SyncAsync(local, [], TestContext.Current.CancellationToken);
        Assert.Equal(22, result.Settings!["editorFontSize"]!.GetValue<int>());
        Assert.Equal("light", result.Settings["colorTheme"]!.GetValue<string>());
        Assert.False(result.Settings["showMinimap"]!.GetValue<bool>());
        Assert.Equal(["editorFontSize"], result.Replaced);
        Assert.NotNull(result.Backup);
        Assert.Contains("16", File.ReadAllText(result.Backup!), StringComparison.Ordinal);
        Assert.True(result.Pushed);

        var onA = await a.SyncAsync(Settings(22, "light"), [], TestContext.Current.CancellationToken);
        Assert.False(onA.Settings!["showMinimap"]!.GetValue<bool>());
        Assert.Empty(onA.Replaced);
    }

    [Fact]
    public async Task Uninstalling_on_one_machine_uninstalls_on_the_others_but_never_on_a_first_sync()
    {
        using var a = await SignedInAsync("a");
        using var b = await SignedInAsync("b");
        SyncedExtension night = new("acme.night", "1.0.0", "Open VSX");
        await a.SyncAsync(Settings(14, "dark"), [night], TestContext.Current.CancellationToken);
        await b.SyncAsync(Settings(14, "dark"), [night], TestContext.Current.CancellationToken);

        _time.Advance(TimeSpan.FromMinutes(1));
        var removed = await a.SyncAsync(Settings(14, "dark"), [], TestContext.Current.CancellationToken);
        Assert.True(removed.Pushed);
        Assert.Contains("\"removed\": true", _github.Gists.Values.Single().Files[SettingsSyncService.ExtensionsFile], StringComparison.Ordinal);

        _time.Advance(TimeSpan.FromMinutes(1));
        var onB = await b.SyncAsync(Settings(14, "dark"), [night], TestContext.Current.CancellationToken);
        Assert.Equal(["acme.night"], onB.ToUninstall);
        Assert.Empty(onB.ToInstall);

        // Once gone, it isn't asked for again; a newer version installed here isn't a change either.
        var after = await b.SyncAsync(Settings(14, "dark"), [], TestContext.Current.CancellationToken);
        Assert.Empty(after.ToUninstall);
        Assert.Empty(after.ToInstall);

        // A third machine that has it keeps it (and brings it back for the others).
        _time.Advance(TimeSpan.FromMinutes(1));
        using var c = await SignedInAsync("c");
        var onC = await c.SyncAsync(Settings(14, "dark"), [night with { Version = "1.1.0" }], TestContext.Current.CancellationToken);
        Assert.Empty(onC.ToUninstall);
        var reinstalled = await a.SyncAsync(Settings(14, "dark"), [], TestContext.Current.CancellationToken);
        Assert.Equal("1.1.0", Assert.Single(reinstalled.ToInstall).Version);
    }

    [Fact]
    public async Task Local_changes_survive_being_offline_and_a_deleted_gist_is_created_again()
    {
        using var a = await SignedInAsync("a");
        await a.SyncAsync(Settings(14, "dark"), [], TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromMinutes(1));
        await Assert.ThrowsAsync<SyncException>(() => Offline(() => a.SyncAsync(Settings(30, "dark"), [], TestContext.Current.CancellationToken)));

        _github.Gists.Clear(); // deleted on github.com meanwhile
        var result = await a.SyncAsync(Settings(30, "dark"), [], TestContext.Current.CancellationToken);
        Assert.True(result.Pushed);
        Assert.Contains("\"editorFontSize\": 30", Assert.Single(_github.Gists.Values).Files[SettingsSyncService.SettingsFile], StringComparison.Ordinal);

        // A restart reads the state back (the pending change was remembered on disk).
        using var again = new SettingsSyncService(new HttpClient(_github), "client-123", Path.Combine(_root, "a"), new MemoryTokenStore(), _time);
        Assert.Equal(a.LastSync, again.LastSync);
    }

    [Fact]
    public async Task A_revoked_token_is_reported_as_needing_a_new_sign_in()
    {
        using var a = await SignedInAsync("a");
        _github.RevokeTokens = true;
        var failure = await Assert.ThrowsAsync<SyncException>(() => a.SyncAsync(Settings(14, "dark"), [], TestContext.Current.CancellationToken));
        Assert.True(failure.IsUnauthorized);
    }

    [Fact]
    public void The_token_file_is_encrypted_for_the_windows_user()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI is Windows only");
        var store = new DpapiTokenStore(Path.Combine(_root, "token.bin"));
        Assert.Null(store.Load());
        store.Save(new GitHubSignIn("octocat", "gho_secret_value"));
        Assert.DoesNotContain("gho_secret_value", Encoding.UTF8.GetString(File.ReadAllBytes(store.FilePath)), StringComparison.Ordinal);
        Assert.Equal(new GitHubSignIn("octocat", "gho_secret_value"), new DpapiTokenStore(store.FilePath).Load());

        File.WriteAllBytes(store.FilePath, [1, 2, 3]); // damaged: as if signed out
        Assert.Null(store.Load());
        store.Delete();
        Assert.False(File.Exists(store.FilePath));
    }

    private async Task<SettingsSyncService> SignedInAsync(string machine, ITokenStore? tokens = null)
    {
        _github.TokenAnswers.Enqueue("""{"access_token":"gho_secret"}""");
        var sync = new SettingsSyncService(new HttpClient(_github), "client-123", Path.Combine(_root, machine), tokens ?? new MemoryTokenStore(), _time, (_, _) => Task.CompletedTask);
        var code = await sync.BeginSignInAsync(TestContext.Current.CancellationToken);
        await sync.CompleteSignInAsync(code, TestContext.Current.CancellationToken);
        return sync;
    }

    private async Task Offline(Func<Task> action)
    {
        _github.Offline = true;
        try
        {
            await action();
        }
        finally
        {
            _github.Offline = false;
        }
    }

    private static JsonObject Settings(int fontSize, string theme) => new()
    {
        ["editorFontSize"] = fontSize,
        ["colorTheme"] = theme,
        ["showMinimap"] = true,
    };

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>Enough of github.com and api.github.com for the device flow and gists, in memory.</summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        private int _nextId;

        public Queue<string> TokenAnswers { get; } = new();

        public List<string> AvatarRequests { get; } = [];

        public string DeviceCodeRequest { get; private set; } = string.Empty;

        public Dictionary<string, FakeGist> Gists { get; } = [];

        public bool Offline { get; set; }

        public bool RevokeTokens { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Offline)
            {
                throw new HttpRequestException("offline");
            }

            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            if (request.RequestUri.Host == "avatars.githubusercontent.com")
            {
                AvatarRequests.Add(request.RequestUri.Query);
                Assert.Null(request.Headers.Authorization); // the token only ever goes to the API
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(AvatarBytes) };
            }

            if (request.RequestUri.Host == "github.com")
            {
                Assert.Equal("application/json", Assert.Single(request.Headers.Accept).MediaType);
                if (path == "/login/device/code")
                {
                    DeviceCodeRequest = body;
                    return Json("""{"device_code":"dev-1","user_code":"ABCD-1234","verification_uri":"https://github.com/login/device","expires_in":900,"interval":5}""");
                }

                Assert.Contains("device_code=dev-1", body, StringComparison.Ordinal);
                return Json(TokenAnswers.Dequeue());
            }

            if (RevokeTokens || request.Headers.Authorization?.ToString() != "Bearer gho_secret")
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            if (path == "/user")
            {
                return Json($$"""{"login":"octocat","name":"The Octocat","avatar_url":"{{Avatar}}"}""");
            }

            if (path == "/gists" && request.Method == HttpMethod.Get)
            {
                return Json(new JsonArray([.. Gists.Values.Select(g => g.ToJson())]).ToJsonString());
            }

            if (path == "/gists" && request.Method == HttpMethod.Post)
            {
                var json = JsonNode.Parse(body)!;
                var gist = new FakeGist($"g{++_nextId}", json["public"]!.GetValue<bool>());
                Apply(gist, json);
                Gists[gist.Id] = gist;
                return Json(gist.ToJson().ToJsonString(), HttpStatusCode.Created);
            }

            var id = path["/gists/".Length..];
            if (!Gists.TryGetValue(id, out var found))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (request.Method == HttpMethod.Patch)
            {
                Apply(found, JsonNode.Parse(body)!);
            }

            return Json(found.ToJson().ToJsonString());
        }

        private static void Apply(FakeGist gist, JsonNode json)
        {
            foreach (var (name, file) in json["files"]!.AsObject())
            {
                gist.Files[name] = file!["content"]!.GetValue<string>();
            }
        }

        private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private sealed class FakeGist(string id, bool isPublic)
    {
        public string Id => id;

        public bool Public => isPublic;

        public Dictionary<string, string> Files { get; } = [];

        public JsonObject ToJson() => new()
        {
            ["id"] = id,
            ["public"] = isPublic,
            ["files"] = new JsonObject(Files.Select(f => KeyValuePair.Create<string, JsonNode?>(f.Key, new JsonObject { ["filename"] = f.Key, ["content"] = f.Value }))),
        };
    }
}
