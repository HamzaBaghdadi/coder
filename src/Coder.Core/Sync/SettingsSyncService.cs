using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Coder.Core.Sync;

/// <summary>An installed extension as Settings Sync sees it.</summary>
/// <param name="Source">Where it was installed from ("Open VSX", "Coder"), when known.</param>
public sealed record SyncedExtension(string Id, string Version, string? Source);

/// <summary>What a sync found: what to apply here, and what it replaced.</summary>
/// <param name="Settings">The merged settings, when they differ from the ones passed in (apply them); null when nothing changed.</param>
/// <param name="ToInstall">Extensions installed on another machine and missing here.</param>
/// <param name="ToUninstall">Extensions uninstalled on another machine and still here.</param>
/// <param name="Replaced">Settings whose local value was replaced by the synced one (a newer change elsewhere, or the first sync).</param>
/// <param name="Backup">Where the local settings were saved before anything was replaced.</param>
public sealed record SyncResult(
    JsonObject? Settings,
    IReadOnlyList<SyncedExtension> ToInstall,
    IReadOnlyList<string> ToUninstall,
    IReadOnlyList<string> Replaced,
    bool IsFirstSync,
    bool Pushed,
    string? Backup);

/// <summary>
/// Settings Sync, without any UI: signs in with GitHub (device flow), and keeps a secret gist holding the
/// user's settings and the list of installed extensions in step with this machine.
/// <para>
/// Merging is last-write-wins per setting key and per extension, by the time each was changed. Local
/// changes are stamped when a sync notices them (diffing against what was last synced), and remembered
/// in <c>state.json</c> so they survive being offline. On the first sync of a machine the synced values
/// win (a new machine's defaults must not overwrite them); anything replaced is reported, and the local
/// settings are backed up first, so nothing is lost silently.
/// </para>
/// </summary>
public sealed class SettingsSyncService : IDisposable
{
    /// <summary>The gist's first file, so it is the gist's name on github.com and how Coder finds it again.</summary>
    public const string MarkerFile = "coder-settings-sync.md";
    public const string SettingsFile = "settings.json";
    public const string ExtensionsFile = "extensions.json";
    public const string GistDescription = "Coder Settings Sync: settings and extensions synced by Coder (github.com/HamzaBaghdadi/coder)";

    private const int MaxBackups = 10;

    private readonly HttpClient _http;
    private readonly string _folder;
    private readonly ITokenStore _tokens;
    private readonly TimeProvider _time;
    private readonly string _apiBase;
    private readonly GitHubDeviceFlow _flow;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GitHubSignIn? _signIn;
    private SyncState _state;

    /// <param name="clientId">The GitHub OAuth app's client id (public; device flow needs no secret). Empty: sync isn't available.</param>
    /// <param name="folder">Where the sync state and backups live (in the user data folder).</param>
    public SettingsSyncService(HttpClient http, string clientId, string folder, ITokenStore tokens, TimeProvider? time = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, string loginBase = "https://github.com", string apiBase = "https://api.github.com")
    {
        _http = http;
        ClientId = clientId;
        _folder = folder;
        _tokens = tokens;
        _time = time ?? TimeProvider.System;
        _apiBase = apiBase;
        _flow = new GitHubDeviceFlow(http, clientId, loginBase, apiBase) { Delay = delay ?? Task.Delay };
        _signIn = tokens.Load();
        _state = LoadState();
        if (_signIn is not null && _state.Login is { } login && !string.Equals(login, _signIn.Login, StringComparison.OrdinalIgnoreCase))
        {
            _state = new SyncState();
        }
    }

    public string ClientId { get; }

    /// <summary>False when this build has no GitHub client id, so signing in can't work.</summary>
    public bool IsConfigured => ClientId.Length > 0;

    public bool IsSignedIn => _signIn is not null;

    /// <summary>The GitHub account signed in, e.g. "octocat".</summary>
    public string? Login => _signIn?.Login;

    /// <summary>Signed in and syncing ("Turn Off Sync" keeps the sign-in but stops syncing).</summary>
    public bool IsEnabled
    {
        get => IsSignedIn && _state.Enabled;
        set
        {
            _state.Enabled = value;
            SaveState();
        }
    }

    public DateTimeOffset? LastSync => _state.LastSync;

    public string StatePath => Path.Combine(_folder, "state.json");

    public Task<DeviceCode> BeginSignInAsync(CancellationToken cancellationToken = default) =>
        IsConfigured ? _flow.StartAsync(cancellationToken) : throw new SyncException("This build of Coder has no GitHub app to sign in with.");

    /// <summary>Waits for the user to enter the code on GitHub, then remembers the account (the token encrypted).</summary>
    public async Task<string> CompleteSignInAsync(DeviceCode code, CancellationToken cancellationToken = default)
    {
        var token = await _flow.WaitForTokenAsync(code, cancellationToken);
        var account = await _flow.GetAccountAsync(token, cancellationToken);
        var login = account.Login;
        if (_state.Login is { } previous && !string.Equals(previous, login, StringComparison.OrdinalIgnoreCase))
        {
            _state = new SyncState(); // another account: its own gist, its own history
            DeleteAvatar();
        }

        _signIn = new GitHubSignIn(login, token);
        _tokens.Save(_signIn);
        _state.Login = login;
        _state.Name = account.Name;
        _state.AvatarUrl = account.AvatarUrl;
        _state.Enabled = true;
        SaveState();
        await DownloadAvatarAsync(cancellationToken);
        return login;
    }

    /// <summary>The account's display name on GitHub, when it has one.</summary>
    public string? Name => IsSignedIn ? _state.Name : null;

    /// <summary>The account's picture, saved next to the sync state; null when signed out or not downloaded (yet).</summary>
    public string? AvatarPath => IsSignedIn && File.Exists(AvatarFile) ? AvatarFile : null;

    private string AvatarFile => Path.Combine(_folder, "avatar.png");

    /// <summary>Downloads the account's picture when it is missing (a sign-in from an older version, or offline then).</summary>
    public async Task<bool> RefreshAvatarAsync(CancellationToken cancellationToken = default)
    {
        if (_signIn is not { } signIn || AvatarPath is not null)
        {
            return false;
        }

        if (_state.AvatarUrl is null)
        {
            try
            {
                var account = await _flow.GetAccountAsync(signIn.Token, cancellationToken);
                _state.Name = account.Name;
                _state.AvatarUrl = account.AvatarUrl;
                SaveState();
            }
            catch (SyncException)
            {
                return false; // offline: the initial stands in until the next start
            }
        }

        return await DownloadAvatarAsync(cancellationToken);
    }

    /// <summary>A small copy of the picture (64 px); best effort, the account shows its initial without it.</summary>
    private async Task<bool> DownloadAvatarAsync(CancellationToken cancellationToken)
    {
        if (_state.AvatarUrl is not { } url || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        try
        {
            var sized = url + (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "s=64";
            using var response = await _http.GetAsync(sized, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 1024 * 1024)
            {
                return false;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length is 0 or > 1024 * 1024)
            {
                return false;
            }

            Directory.CreateDirectory(_folder);
            await File.WriteAllBytesAsync(AvatarFile + ".tmp", bytes, cancellationToken);
            File.Move(AvatarFile + ".tmp", AvatarFile, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException
                                              or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private void DeleteAvatar()
    {
        try
        {
            File.Delete(AvatarFile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // replaced on the next sign-in
        }
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>Forgets the token and the sync history (the gist stays on GitHub).</summary>
    public void SignOut()
    {
        _tokens.Delete();
        _signIn = null;
        _state = new SyncState();
        DeleteAvatar();
        try
        {
            File.Delete(StatePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // an empty state replaces it on the next save
        }
    }

    /// <summary>
    /// Pulls, merges and pushes. <paramref name="settings"/> are this machine's settings (camelCase JSON,
    /// machine-specific keys already left out); <paramref name="extensions"/> what is installed here.
    /// </summary>
    /// <exception cref="SyncException">Not signed in, offline, or GitHub refused (see <see cref="SyncException.IsUnauthorized"/>).</exception>
    public async Task<SyncResult> SyncAsync(JsonObject settings, IReadOnlyList<SyncedExtension> extensions, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var signIn = _signIn ?? throw new SyncException("Sign in with GitHub to sync settings.");
            var now = _time.GetUtcNow();
            var first = _state.LastSync is null;
            RecordLocal(settings, extensions, now, first);
            SaveState(); // local changes are remembered even if GitHub can't be reached

            var gist = new GistClient(_http, signIn.Token, _apiBase);
            var id = _state.GistId ?? await gist.FindAsync(MarkerFile, cancellationToken);
            var remote = id is null ? null : await gist.ReadAsync(id, cancellationToken);
            if (remote is null)
            {
                id = _state.GistId is null ? null : await gist.FindAsync(MarkerFile, cancellationToken); // deleted on github.com: look again, else start anew
                remote = id is null ? null : await gist.ReadAsync(id, cancellationToken);
            }

            var remoteSettingsText = remote?.Files.GetValueOrDefault(SettingsFile);
            var remoteExtensionsText = remote?.Files.GetValueOrDefault(ExtensionsFile);
            var replaced = MergeSettings(ParseSettings(remoteSettingsText), first);
            MergeExtensions(ParseExtensions(remoteExtensionsText), first);

            // What the gist should hold now; keys only this machine has are stamped now, so they win over another machine's first sync.
            foreach (var stamp in _state.Settings.Values.Where(s => s.Modified == DateTimeOffset.MinValue))
            {
                stamp.Modified = now;
            }

            foreach (var stamp in _state.Extensions.Values.Where(s => s.Modified == DateTimeOffset.MinValue))
            {
                stamp.Modified = now;
            }

            var settingsText = FormatSettings();
            var extensionsText = FormatExtensions();
            var pushed = false;
            if (id is null)
            {
                id = await gist.CreateAsync(GistDescription, new Dictionary<string, string>
                {
                    [MarkerFile] = MarkerText,
                    [SettingsFile] = settingsText,
                    [ExtensionsFile] = extensionsText,
                }, cancellationToken);
                pushed = true;
            }
            else
            {
                var changed = new Dictionary<string, string>();
                if (settingsText != remoteSettingsText)
                {
                    changed[SettingsFile] = settingsText;
                }

                if (extensionsText != remoteExtensionsText)
                {
                    changed[ExtensionsFile] = extensionsText;
                }

                if (changed.Count > 0)
                {
                    await gist.UpdateAsync(id, changed, cancellationToken);
                    pushed = true;
                }
            }

            var merged = new JsonObject(_state.Settings.OrderBy(s => s.Key, StringComparer.Ordinal)
                .Select(s => KeyValuePair.Create(s.Key, s.Value.Value?.DeepClone())));
            var differs = merged.Any(pair => !settings.TryGetPropertyValue(pair.Key, out var local) || !JsonNode.DeepEquals(local, pair.Value));
            var backup = replaced.Count > 0 ? Backup(settings, now) : null;

            foreach (var stamp in _state.Settings.Values)
            {
                stamp.Pending = false;
            }

            foreach (var stamp in _state.Extensions.Values)
            {
                stamp.Pending = false;
            }

            _state.GistId = id;
            _state.LastSync = now;
            SaveState();
            return new SyncResult(
                differs ? merged : null,
                _state.Extensions.Where(e => !e.Value.Removed && !e.Value.Local).Select(e => new SyncedExtension(e.Key, e.Value.Version, e.Value.Source)).OrderBy(e => e.Id, StringComparer.Ordinal).ToList(),
                _state.Extensions.Where(e => e.Value.Removed && e.Value.Local).Select(e => e.Key).Order(StringComparer.Ordinal).ToList(),
                replaced,
                first,
                pushed,
                backup);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Stamps what changed here since the last sync. On a machine's first sync nothing is stamped (the
    /// synced values win), so its defaults never overwrite what other machines chose.
    /// </summary>
    private void RecordLocal(JsonObject settings, IReadOnlyList<SyncedExtension> extensions, DateTimeOffset now, bool first)
    {
        var stamp = first ? DateTimeOffset.MinValue : now;
        foreach (var (key, value) in settings)
        {
            if (!_state.Settings.TryGetValue(key, out var known))
            {
                _state.Settings[key] = new SettingStamp { Value = value?.DeepClone(), Modified = stamp, Pending = !first };
            }
            else if (!JsonNode.DeepEquals(known.Value, value))
            {
                known.Value = value?.DeepClone();
                known.Modified = now;
                known.Pending = true;
            }
        }

        var installed = extensions.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var extension in extensions)
        {
            if (!_state.Extensions.TryGetValue(extension.Id, out var known))
            {
                _state.Extensions[extension.Id] = new ExtensionStamp { Version = extension.Version, Source = extension.Source, Modified = stamp, Pending = !first, Local = true };
                continue;
            }

            if (known.Removed && !known.Local)
            {
                known.Removed = false; // installed again here
                known.Modified = now;
                known.Pending = true;
            }

            // Versions follow along but are not changes: each machine installs the latest, and that must not ping-pong.
            known.Local = true;
            known.Version = extension.Version;
            known.Source = extension.Source ?? known.Source;
        }

        foreach (var (id, known) in _state.Extensions)
        {
            if (known.Local && !installed.ContainsKey(id))
            {
                known.Local = false;
                if (!known.Removed)
                {
                    known.Removed = true; // uninstalled here
                    known.Modified = now;
                    known.Pending = true;
                }
            }
        }
    }

    /// <summary>Last write wins per key; a tie goes to the synced value. Returns the local keys that were replaced.</summary>
    private List<string> MergeSettings(SyncedSettingsFile remote, bool first)
    {
        var replaced = new List<string>();
        foreach (var (key, value) in remote.Settings)
        {
            var modified = remote.Modified.GetValueOrDefault(key);
            if (!_state.Settings.TryGetValue(key, out var local))
            {
                _state.Settings[key] = new SettingStamp { Value = value?.DeepClone(), Modified = modified };
            }
            else if (JsonNode.DeepEquals(local.Value, value))
            {
                local.Modified = local.Modified > modified ? local.Modified : modified;
            }
            else if (local.Modified <= modified)
            {
                if (local.Pending || first)
                {
                    replaced.Add(key);
                }

                local.Value = value?.DeepClone();
                local.Modified = modified;
            }
        }

        return replaced;
    }

    /// <summary>Last write wins per extension; on a machine's first sync what is installed here stays (and is added).</summary>
    private void MergeExtensions(SyncedExtensionsFile remote, bool first)
    {
        foreach (var entry in remote.Extensions.Where(e => e.Id is { Length: > 0 }))
        {
            if (!_state.Extensions.TryGetValue(entry.Id, out var local))
            {
                _state.Extensions[entry.Id] = new ExtensionStamp { Version = entry.Version, Source = entry.Source, Removed = entry.Removed, Modified = entry.Modified };
            }
            else if (local.Removed == entry.Removed)
            {
                local.Modified = local.Modified > entry.Modified ? local.Modified : entry.Modified;
            }
            else if (local.Modified <= entry.Modified && !(first && local.Local))
            {
                local.Removed = entry.Removed;
                local.Modified = entry.Modified;
            }

            if (_state.Extensions[entry.Id] is { Local: false } missing)
            {
                missing.Version = entry.Version;
                missing.Source = entry.Source ?? missing.Source;
            }
        }
    }

    private static SyncedSettingsFile ParseSettings(string? text)
    {
        try
        {
            return text is null ? new() : JsonSerializer.Deserialize(text, SyncJson.Default.SyncedSettingsFile) ?? new();
        }
        catch (JsonException)
        {
            return new(); // damaged by hand: this machine's settings replace it
        }
    }

    private static SyncedExtensionsFile ParseExtensions(string? text)
    {
        try
        {
            return text is null ? new() : JsonSerializer.Deserialize(text, SyncJson.Default.SyncedExtensionsFile) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    private string FormatSettings()
    {
        var file = new SyncedSettingsFile();
        foreach (var (key, stamp) in _state.Settings.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            file.Settings[key] = stamp.Value?.DeepClone();
            file.Modified[key] = stamp.Modified;
        }

        return JsonSerializer.Serialize(file, SyncJson.Default.SyncedSettingsFile);
    }

    private string FormatExtensions() => JsonSerializer.Serialize(new SyncedExtensionsFile
    {
        Extensions = _state.Extensions.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
            .Select(e => new SyncedExtensionEntry { Id = e.Key, Version = e.Value.Version, Source = e.Value.Source, Removed = e.Value.Removed, Modified = e.Value.Modified })
            .ToList(),
    }, SyncJson.Default.SyncedExtensionsFile);

    /// <summary>Saves this machine's settings before synced ones replace any (the newest few are kept).</summary>
    private string? Backup(JsonObject settings, DateTimeOffset now)
    {
        try
        {
            var folder = Path.Combine(_folder, "backups");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"settings-{now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.json");
            File.WriteAllText(path, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            foreach (var old in Directory.GetFiles(folder, "settings-*.json").Order(StringComparer.Ordinal).SkipLast(MaxBackups))
            {
                File.Delete(old);
            }

            return path;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private SyncState LoadState()
    {
        try
        {
            var state = File.Exists(StatePath) ? JsonSerializer.Deserialize(File.ReadAllText(StatePath), SyncJson.Default.SyncState) ?? new() : new();
            state.Settings = new(state.Settings ?? [], StringComparer.Ordinal);
            state.Extensions = new(state.Extensions ?? [], StringComparer.OrdinalIgnoreCase);
            return state;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    private void SaveState()
    {
        try
        {
            Directory.CreateDirectory(_folder);
            var temp = StatePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_state, SyncJson.Default.SyncState));
            File.Move(temp, StatePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // the next sync tries again
        }
    }

    private const string MarkerText = """
        # Coder Settings Sync

        This secret gist is where [Coder](https://github.com/HamzaBaghdadi/coder) keeps your settings
        (`settings.json`) and the list of extensions you installed (`extensions.json`), so they follow you
        to every machine you sign in on. Coder rewrites it as you change things; deleting it turns the
        synced copy off until Coder creates a new one.
        """;
}

/// <summary>What <c>state.json</c> remembers between sessions.</summary>
internal sealed class SyncState
{
    public bool Enabled { get; set; } = true;

    public string? Login { get; set; }

    public string? Name { get; set; }

    public string? AvatarUrl { get; set; }

    public string? GistId { get; set; }

    public DateTimeOffset? LastSync { get; set; }

    public Dictionary<string, SettingStamp> Settings { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, ExtensionStamp> Extensions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class SettingStamp
{
    public JsonNode? Value { get; set; }

    public DateTimeOffset Modified { get; set; }

    /// <summary>Changed here and not synced yet.</summary>
    public bool Pending { get; set; }
}

internal sealed class ExtensionStamp
{
    public string Version { get; set; } = string.Empty;

    public string? Source { get; set; }

    /// <summary>Uninstalled (a tombstone, so the uninstall reaches other machines).</summary>
    public bool Removed { get; set; }

    public DateTimeOffset Modified { get; set; }

    public bool Pending { get; set; }

    /// <summary>Installed on this machine.</summary>
    public bool Local { get; set; }
}

/// <summary>The gist's settings.json: the settings, and when each was last changed.</summary>
internal sealed class SyncedSettingsFile
{
    public int Version { get; set; } = 1;

    public Dictionary<string, JsonNode?> Settings { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, DateTimeOffset> Modified { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>The gist's extensions.json: installed extensions, and uninstalled ones (so uninstalls sync too).</summary>
internal sealed class SyncedExtensionsFile
{
    public int Version { get; set; } = 1;

    public List<SyncedExtensionEntry> Extensions { get; set; } = [];
}

internal sealed class SyncedExtensionEntry
{
    public string Id { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string? Source { get; set; }

    public bool Removed { get; set; }

    public DateTimeOffset Modified { get; set; }
}
