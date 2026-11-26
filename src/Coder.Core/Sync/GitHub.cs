using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Coder.Core.Sync;

/// <summary>Signing in or syncing failed; the message says why, for the user.</summary>
public sealed class SyncException(string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>GitHub no longer accepts the token (revoked, expired): the user has to sign in again.</summary>
    public bool IsUnauthorized { get; init; }
}

/// <summary>Who signed in: the login ("octocat"), the display name when they set one, and the picture's address.</summary>
public sealed record GitHubAccount(string Login, string? Name, string? AvatarUrl);

/// <summary>What the user types on github.com/login/device, and what Coder polls with meanwhile.</summary>
public sealed record DeviceCode(string Code, string UserCode, Uri VerificationUri, TimeSpan Interval, DateTimeOffset ExpiresAt);

/// <summary>
/// GitHub's OAuth device flow (no client secret, no embedded browser): Coder asks for a code, the user
/// enters it on github.com/login/device, and Coder polls until GitHub hands over a token. Only the
/// <c>gist</c> scope is asked for: the token can read and write the user's gists, nothing else.
/// </summary>
public sealed class GitHubDeviceFlow(HttpClient http, string clientId, string loginBase = "https://github.com", string apiBase = "https://api.github.com")
{
    public const string Scope = "gist";

    /// <summary>How the flow waits between polls; tests replace it to run instantly.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    public async Task<DeviceCode> StartAsync(CancellationToken cancellationToken = default)
    {
        var json = await PostFormAsync($"{loginBase}/login/device/code", new() { ["client_id"] = clientId, ["scope"] = Scope }, cancellationToken);
        var code = json.Deserialize(SyncJson.Default.DeviceCodeResponse);
        if (code?.DeviceCode is not { Length: > 0 } device || code.UserCode is not { Length: > 0 } user)
        {
            throw new SyncException(json["error_description"]?.GetValue<string>() is { } why
                ? $"GitHub didn't start the sign-in: {why}"
                : "GitHub didn't start the sign-in. Try again in a moment.");
        }

        return new DeviceCode(device, user,
            Uri.TryCreate(code.VerificationUri, UriKind.Absolute, out var uri) ? uri : new Uri($"{loginBase}/login/device"),
            TimeSpan.FromSeconds(Math.Max(code.Interval, 1)),
            DateTimeOffset.UtcNow.AddSeconds(code.ExpiresIn > 0 ? code.ExpiresIn : 900));
    }

    /// <summary>Polls until the user has entered the code (or declined, or it expired); returns the access token.</summary>
    public async Task<string> WaitForTokenAsync(DeviceCode code, CancellationToken cancellationToken = default)
    {
        var interval = code.Interval;
        while (true)
        {
            await Delay(interval, cancellationToken);
            var json = await PostFormAsync($"{loginBase}/login/oauth/access_token", new()
            {
                ["client_id"] = clientId,
                ["device_code"] = code.Code,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            }, cancellationToken);
            if (json["access_token"]?.GetValue<string>() is { Length: > 0 } token)
            {
                return token;
            }

            switch (json["error"]?.GetValue<string>())
            {
                case "authorization_pending":
                    break;
                case "slow_down":
                    // GitHub says by how much; the spec says five seconds more.
                    interval = json["interval"]?.GetValueKind() == JsonValueKind.Number ? TimeSpan.FromSeconds(json["interval"]!.GetValue<int>()) : interval + TimeSpan.FromSeconds(5);
                    break;
                case "expired_token":
                    throw new SyncException("The sign-in code expired before it was entered. Sign in again for a new one.");
                case "access_denied":
                    throw new SyncException("Sign-in was cancelled on GitHub.");
                case var other:
                    throw new SyncException($"GitHub refused the sign-in ({other ?? "no reason given"}).");
            }
        }
    }

    /// <summary>The account the token belongs to (its login, e.g. "octocat").</summary>
    public async Task<GitHubAccount> GetAccountAsync(string token, CancellationToken cancellationToken = default)
    {
        using var request = GistClient.Request(HttpMethod.Get, $"{apiBase}/user", token);
        var user = await GistClient.SendAsync(http, request, SyncJson.Default.GitHubUser, cancellationToken);
        return user?.Login is { Length: > 0 } login
            ? new GitHubAccount(login, user.Name is { Length: > 0 } name ? name : null, user.AvatarUrl)
            : throw new SyncException("GitHub didn't say which account signed in.");
    }

    private async Task<JsonObject> PostFormAsync(string url, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.BadRequest)
            {
                throw new SyncException(response.StatusCode == HttpStatusCode.NotFound
                    ? "GitHub doesn't know this app's sign-in (is device flow enabled for the OAuth app?)."
                    : $"GitHub answered {(int)response.StatusCode} to the sign-in. Try again in a moment.");
            }

            return JsonNode.Parse(text) as JsonObject ?? [];
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new SyncException("GitHub couldn't be reached. Check your connection and try again.", exception);
        }
    }
}

/// <summary>A gist: its id and its files (name to content).</summary>
public sealed record Gist(string Id, IReadOnlyDictionary<string, string> Files);

/// <summary>The few gist calls Settings Sync needs: find, read, create and update a secret gist.</summary>
public sealed class GistClient(HttpClient http, string token, string apiBase = "https://api.github.com")
{
    private const int MaxPages = 10;

    /// <summary>The user's gist that holds a file with this name, or null.</summary>
    public async Task<string?> FindAsync(string fileName, CancellationToken cancellationToken = default)
    {
        for (var page = 1; page <= MaxPages; page++)
        {
            using var request = Request(HttpMethod.Get, $"{apiBase}/gists?per_page=100&page={page}", token);
            var gists = await SendAsync(http, request, SyncJson.Default.ListGistResponse, cancellationToken) ?? [];
            if (gists.FirstOrDefault(g => g.Files?.ContainsKey(fileName) == true) is { Id: { } id })
            {
                return id;
            }

            if (gists.Count < 100)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>The gist's files, or null when it no longer exists (deleted on github.com).</summary>
    public async Task<Gist?> ReadAsync(string id, CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Get, $"{apiBase}/gists/{Uri.EscapeDataString(id)}", token);
        try
        {
            var gist = await SendAsync(http, request, SyncJson.Default.GistResponse, cancellationToken);
            return gist is null ? null : await ToGistAsync(gist, cancellationToken);
        }
        catch (SyncException exception) when (exception.InnerException is HttpRequestException { StatusCode: HttpStatusCode.NotFound })
        {
            return null;
        }
    }

    /// <summary>Creates a secret gist (only its owner sees it in their list; anyone with the address can read it).</summary>
    public async Task<string> CreateAsync(string description, IReadOnlyDictionary<string, string> files, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject { ["description"] = description, ["public"] = false, ["files"] = Files(files) };
        using var request = Request(HttpMethod.Post, $"{apiBase}/gists", token, body);
        var gist = await SendAsync(http, request, SyncJson.Default.GistResponse, cancellationToken);
        return gist?.Id ?? throw new SyncException("GitHub didn't create the settings gist.");
    }

    /// <summary>Replaces the named files' contents (other files stay).</summary>
    public async Task UpdateAsync(string id, IReadOnlyDictionary<string, string> files, CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Patch, $"{apiBase}/gists/{Uri.EscapeDataString(id)}", token, new JsonObject { ["files"] = Files(files) });
        await SendAsync(http, request, SyncJson.Default.GistResponse, cancellationToken);
    }

    private static JsonObject Files(IReadOnlyDictionary<string, string> files) =>
        new(files.Select(f => KeyValuePair.Create<string, JsonNode?>(f.Key, new JsonObject { ["content"] = f.Value })));

    /// <summary>Large files come truncated in the gist; their full text is at raw_url.</summary>
    private async Task<Gist> ToGistAsync(GistResponse gist, CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, file) in gist.Files ?? [])
        {
            if (file.Truncated && file.RawUrl is { } raw)
            {
                using var request = Request(HttpMethod.Get, raw, token);
                using var response = await http.SendAsync(request, cancellationToken);
                files[name] = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            else
            {
                files[name] = file.Content ?? string.Empty;
            }
        }

        return new Gist(gist.Id ?? string.Empty, files);
    }

    internal static HttpRequestMessage Request(HttpMethod method, string url, string token, JsonObject? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        }

        return request;
    }

    internal static async Task<T?> SendAsync<T>(HttpClient http, HttpRequestMessage request, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new SyncException("GitHub no longer accepts Coder's sign-in. Sign in again to keep syncing.") { IsUnauthorized = true };
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new SyncException($"GitHub answered {(int)response.StatusCode} ({response.ReasonPhrase}).",
                    new HttpRequestException(response.ReasonPhrase, null, response.StatusCode));
            }

            return await response.Content.ReadFromJsonAsync(type, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new SyncException("GitHub couldn't be reached. Check your connection and try again.", exception);
        }
    }
}

internal sealed class DeviceCodeResponse
{
    [JsonPropertyName("device_code")]
    public string? DeviceCode { get; set; }

    [JsonPropertyName("user_code")]
    public string? UserCode { get; set; }

    [JsonPropertyName("verification_uri")]
    public string? VerificationUri { get; set; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("interval")]
    public int Interval { get; set; }
}

internal sealed class GitHubUser
{
    [JsonPropertyName("login")]
    public string? Login { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("avatar_url")]
    public string? AvatarUrl { get; set; }
}

internal sealed class GistResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("files")]
    public Dictionary<string, GistFileResponse>? Files { get; set; }
}

internal sealed class GistFileResponse
{
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; set; }

    [JsonPropertyName("raw_url")]
    public string? RawUrl { get; set; }
}

internal sealed class ListGistResponse : List<GistResponse>;

[JsonSerializable(typeof(DeviceCodeResponse))]
[JsonSerializable(typeof(GitHubUser))]
[JsonSerializable(typeof(GistResponse))]
[JsonSerializable(typeof(ListGistResponse))]
[JsonSerializable(typeof(SyncState))]
[JsonSerializable(typeof(SyncedSettingsFile))]
[JsonSerializable(typeof(SyncedExtensionsFile))]
[JsonSerializable(typeof(StoredToken))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class SyncJson : JsonSerializerContext;
