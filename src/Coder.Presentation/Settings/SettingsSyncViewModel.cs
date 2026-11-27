using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Coder.Core.Extensions;
using Coder.Core.Sync;
using Coder.Presentation.Mvvm;
using Coder.Presentation.Services;

namespace Coder.Presentation.Settings;

/// <summary>
/// Settings Sync, like VS Code's: sign in with GitHub, and settings and installed extensions follow you to
/// every machine you sign in on. Pulls quietly at start and after signing in; pushes a moment after a
/// setting changes or an extension is installed or uninstalled. A synced change that replaces one made
/// here is always said in the status bar (and the local settings are backed up first).
/// </summary>
public sealed class SettingsSyncViewModel : ObservableObject, IDisposable
{
    /// <summary>Keys that belong to this machine: where the window is, what was open, the renderer, the layout of the moment.</summary>
    public static readonly IReadOnlySet<string> MachineKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "window", "recentFolders", "gpuAcceleration", "sideBarWidth", "isSideBarVisible",
    };

    private readonly SettingsSyncService _service;
    private readonly ExtensionStore _store;
    private readonly IReadOnlyList<IExtensionSource> _sources;
    private readonly IDialogService _dialogs;
    private readonly Func<UserSettings> _read;
    private readonly Action<UserSettings> _apply;
    private readonly Action<string> _notify;
    private readonly Action<Uri> _openUrl;
    private readonly Func<string, Task> _copy;
    private readonly TimeSpan _debounce;
    private CancellationTokenSource? _signIn;
    private CancellationTokenSource? _pending;
    private bool _isSyncing;
    private bool _syncAgain;
    private bool _applying;
    private string? _userCode;
    private string _statusText = string.Empty;

    /// <param name="read">This machine's settings as they are now.</param>
    /// <param name="apply">Applies synced settings (machine-specific keys keep their local values).</param>
    /// <param name="notify">The status bar.</param>
    /// <param name="openUrl">Opens GitHub's sign-in page in the browser.</param>
    /// <param name="copy">Puts the sign-in code on the clipboard.</param>
    public SettingsSyncViewModel(SettingsSyncService service, ExtensionStore store, IReadOnlyList<IExtensionSource> sources, IDialogService dialogs,
        Func<UserSettings> read, Action<UserSettings> apply, Action<string> notify, Action<Uri> openUrl, Func<string, Task> copy, TimeSpan? debounce = null)
    {
        _service = service;
        _store = store;
        _sources = sources;
        _dialogs = dialogs;
        _read = read;
        _apply = apply;
        _notify = notify;
        _openUrl = openUrl;
        _copy = copy;
        _debounce = debounce ?? TimeSpan.FromSeconds(2);
        _store.Changed += OnExtensionsChanged;
        _statusText = DescribeState();
    }

    /// <summary>This build can sign in to GitHub (it has an OAuth app's client id).</summary>
    public bool IsAvailable => _service.IsConfigured;

    public bool IsSignedIn => _service.IsSignedIn;

    /// <summary>The GitHub account, e.g. "octocat".</summary>
    public string? Account => _service.Login;

    /// <summary>The account's name on GitHub, else its login.</summary>
    public string? DisplayName => _service.Name ?? _service.Login;

    /// <summary>The account's picture (a local file), for the title bar; null shows the initial instead.</summary>
    public string? AvatarPath => _service.AvatarPath;

    /// <summary>Stands in for the picture: the login's first letter.</summary>
    public string Initial => Account is { Length: > 0 } login ? login[..1].ToUpperInvariant() : string.Empty;

    public bool IsSyncOn => _service.IsEnabled;

    public bool IsSigningIn => _signIn is not null;

    /// <summary>The code to enter on github.com/login/device while signing in.</summary>
    public string? UserCode
    {
        get => _userCode;
        private set => SetProperty(ref _userCode, value);
    }

    public bool IsSyncing
    {
        get => _isSyncing;
        private set => SetProperty(ref _isSyncing, value);
    }

    /// <summary>For the settings page: who is signed in and when settings last synced, or what went wrong.</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>At start: pulls quietly when sync is on.</summary>
    public async Task StartAsync()
    {
        if (IsSignedIn && await _service.RefreshAvatarAsync())
        {
            RaiseState(); // the picture arrived
        }

        if (IsSyncOn)
        {
            await SyncAsync(quiet: true);
        }
    }

    /// <summary>"Sign in with GitHub to Sync Settings…": shows the code, opens GitHub, waits, then syncs.</summary>
    public async Task SignInAsync()
    {
        if (!IsAvailable)
        {
            _notify("Settings Sync isn't available in this build of Coder (it has no GitHub app to sign in with).");
            return;
        }

        if (IsSigningIn)
        {
            _notify(UserCode is { } pending ? $"Enter {pending} on GitHub to finish signing in." : "Signing in to GitHub…");
            return;
        }

        var cancel = _signIn = new CancellationTokenSource();
        RaiseState();
        try
        {
            var code = await _service.BeginSignInAsync(cancel.Token);
            UserCode = code.UserCode;
            StatusText = $"Enter {code.UserCode} on {code.VerificationUri.Host}{code.VerificationUri.AbsolutePath} to sign in.";
            if (!await _dialogs.ConfirmAsync(
                    $"Sign in with GitHub: {code.UserCode}",
                    $"Enter the code {code.UserCode} on GitHub to let Coder sync your settings and extensions through a secret gist in your account. Coder copies the code and opens {code.VerificationUri} for you.",
                    "Copy Code and Open GitHub"))
            {
                _notify("Sign-in cancelled.");
                return;
            }

            await _copy(code.UserCode);
            _openUrl(code.VerificationUri);
            _notify($"Waiting for GitHub: enter {code.UserCode} (copied) in the browser.");
            var login = await _service.CompleteSignInAsync(code, cancel.Token);
            _notify($"Signed in to GitHub as {login}. Syncing settings…");
            EndSignIn(cancel);
            await SyncAsync(quiet: false);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            _notify("Sign-in cancelled.");
        }
        catch (SyncException exception)
        {
            _notify(exception.Message);
        }
        finally
        {
            EndSignIn(cancel);
        }
    }

    public void CancelSignIn() => _signIn?.Cancel();

    /// <summary>"Settings Sync: Sync Now".</summary>
    public Task SyncNowAsync()
    {
        if (!IsSignedIn)
        {
            return SignInAsync();
        }

        if (!IsSyncOn)
        {
            _notify("Settings Sync is off. Turn it on to sync.");
            return Task.CompletedTask;
        }

        return SyncAsync(quiet: false);
    }

    public Task TurnOnAsync()
    {
        if (!IsSignedIn)
        {
            return SignInAsync();
        }

        _service.IsEnabled = true;
        RaiseState();
        return SyncAsync(quiet: false);
    }

    /// <summary>Stops syncing; stays signed in. Nothing here or on GitHub is removed.</summary>
    public void TurnOff()
    {
        _pending?.Cancel();
        _service.IsEnabled = false;
        RaiseState();
        _notify("Settings Sync is off. Your settings stay as they are here and on GitHub.");
    }

    /// <summary>Forgets the GitHub sign-in (the token is deleted; the gist stays on GitHub).</summary>
    public void SignOut()
    {
        _pending?.Cancel();
        var account = Account;
        _service.SignOut();
        RaiseState();
        _notify(account is null ? "Signed out." : $"Signed out of GitHub ({account}). Settings Sync is off.");
    }

    /// <summary>A setting changed here: push it a moment later (changes in quick succession go together).</summary>
    public void NotifyLocalChange()
    {
        if (_applying || !IsSyncOn)
        {
            return;
        }

        _pending?.Cancel();
        _pending?.Dispose();
        var pending = _pending = new CancellationTokenSource();
        _ = PushLaterAsync(pending.Token);
    }

    public void Dispose()
    {
        _store.Changed -= OnExtensionsChanged;
        _pending?.Cancel();
        _pending?.Dispose();
        _signIn?.Cancel();
    }

    private async Task PushLaterAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_debounce, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return; // a newer change restarted the wait
        }

        await SyncAsync(quiet: true);
    }

    /// <summary>Pull, merge, push; then installs and uninstalls extensions and applies settings that came from elsewhere.</summary>
    /// <param name="quiet">At start and after local changes: only what the user must know reaches the status bar.</param>
    public async Task SyncAsync(bool quiet)
    {
        if (!IsSyncOn)
        {
            return;
        }

        if (IsSyncing)
        {
            _syncAgain = true; // a change arrived meanwhile: it goes in the next round
            return;
        }

        IsSyncing = true;
        StatusText = "Syncing…";
        try
        {
            var result = await _service.SyncAsync(ToJson(_read()), LocalExtensions());
            var (installed, removed, failed) = await ApplyExtensionsAsync(result);
            if (result.Settings is { } merged && FromJson(merged, _read()) is { } settings)
            {
                _applying = true;
                try
                {
                    _apply(settings);
                }
                finally
                {
                    _applying = false;
                }
            }

            StatusText = DescribeState();
            Report(result, installed, removed, failed, quiet);
        }
        catch (SyncException exception) when (exception.IsUnauthorized)
        {
            _service.SignOut();
            RaiseState();
            _notify(exception.Message);
        }
        catch (SyncException exception)
        {
            StatusText = $"Not synced: {exception.Message}";
            if (!quiet)
            {
                _notify($"Settings Sync: {exception.Message}");
            }
        }
        finally
        {
            IsSyncing = false;
            if (_syncAgain)
            {
                _syncAgain = false;
                NotifyLocalChange();
            }
        }
    }

    private async Task<(List<string> Installed, List<string> Removed, List<string> Failed)> ApplyExtensionsAsync(SyncResult result)
    {
        List<string> installed = [], removed = [], failed = [];
        _applying = true; // the store's Changed events are this sync's own doing
        try
        {
            foreach (var id in result.ToUninstall)
            {
                try
                {
                    await _store.UninstallAsync(id);
                    removed.Add(id);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    failed.Add(id);
                }
            }

            foreach (var extension in result.ToInstall)
            {
                try
                {
                    if (await FindAsync(extension) is { } listing)
                    {
                        await _store.InstallAsync(listing);
                        installed.Add(listing.DisplayName);
                    }
                    else
                    {
                        failed.Add(extension.Id);
                    }
                }
                catch (ExtensionException)
                {
                    failed.Add(extension.Id);
                }
            }
        }
        finally
        {
            _applying = false;
        }

        if (installed.Count > 0 || removed.Count > 0)
        {
            NotifyLocalChange(); // records them as installed here (only versions may change in the gist)
        }

        return (installed, removed, failed);
    }

    /// <summary>The source it came from first, then the others.</summary>
    private async Task<ExtensionListing?> FindAsync(SyncedExtension extension)
    {
        foreach (var source in _sources.OrderBy(s => s.Name == extension.Source ? 0 : 1))
        {
            try
            {
                if (await source.FindAsync(extension.Id) is { } listing)
                {
                    return listing;
                }
            }
            catch (ExtensionException)
            {
                // try the next source
            }
        }

        return null;
    }

    private void Report(SyncResult result, List<string> installed, List<string> removed, List<string> failed, bool quiet)
    {
        var parts = new List<string>();
        if (result.Replaced.Count > 0)
        {
            var names = Names(result.Replaced);
            parts.Add(result.IsFirstSync
                ? $"Settings Sync applied your synced settings, replacing the ones here ({names})."
                : $"Settings Sync: a newer change from another machine replaced your change to {names}.");
            if (result.Backup is not null)
            {
                parts.Add("The previous settings were backed up.");
            }
        }

        if (installed.Count > 0)
        {
            parts.Add($"Installed {Names(installed)}.");
        }

        if (removed.Count > 0)
        {
            parts.Add($"Uninstalled {Names(removed)}.");
        }

        if (failed.Count > 0)
        {
            parts.Add($"Couldn't install or remove {Names(failed)}.");
        }

        if (parts.Count == 0 && !quiet)
        {
            parts.Add(result.Settings is null && !result.Pushed ? "Settings are in sync." : "Settings synced.");
        }

        if (parts.Count > 0)
        {
            _notify(string.Join(" ", parts));
        }
    }

    private static string Names(List<string> names) => names.Count <= 3 ? string.Join(", ", names) : $"{string.Join(", ", names.Take(3))} and {names.Count - 3} more";

    private static string Names(IReadOnlyList<string> names) => Names(names.ToList());

    private List<SyncedExtension> LocalExtensions() =>
        _store.Installed().Select(e => new SyncedExtension(e.Id, e.Version, e.Source)).ToList();

    private void OnExtensionsChanged(object? sender, EventArgs e) => NotifyLocalChange();

    private void EndSignIn(CancellationTokenSource cancel)
    {
        if (ReferenceEquals(_signIn, cancel))
        {
            _signIn = null;
            cancel.Dispose();
            UserCode = null;
            RaiseState();
        }
    }

    private string DescribeState() =>
        !IsAvailable ? "Not available in this build."
        : !IsSignedIn ? "Sign in with GitHub to keep your settings and extensions the same on every machine."
        : !IsSyncOn ? $"Signed in as {Account}. Sync is off."
        : _service.LastSync is { } last ? $"Signed in as {Account}. Last synced {last.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}."
        : $"Signed in as {Account}.";

    private void RaiseState()
    {
        StatusText = DescribeState();
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(Account));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(AvatarPath));
        OnPropertyChanged(nameof(Initial));
        OnPropertyChanged(nameof(IsSyncOn));
        OnPropertyChanged(nameof(IsSigningIn));
    }

    /// <summary>The settings as Settings Sync sees them: camelCase JSON without this machine's keys.</summary>
    public static JsonObject ToJson(UserSettings settings)
    {
        var json = (JsonObject)JsonSerializer.SerializeToNode(settings, SyncSettingsJson.Default.UserSettings)!;
        foreach (var key in MachineKeys)
        {
            json.Remove(key);
        }

        return json;
    }

    /// <summary>The current settings with the synced keys laid over them; null when they don't make settings.</summary>
    public static UserSettings? FromJson(JsonObject synced, UserSettings current)
    {
        var json = (JsonObject)JsonSerializer.SerializeToNode(current, SyncSettingsJson.Default.UserSettings)!;
        foreach (var (key, value) in synced)
        {
            if (!MachineKeys.Contains(key) && json.ContainsKey(key))
            {
                json[key] = value?.DeepClone(); // keys this version doesn't know stay in the gist, not here
            }
        }

        try
        {
            return json.Deserialize(SyncSettingsJson.Default.UserSettings);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Compile-time JSON for settings, in the same shape as settings.json.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UserSettings))]
internal sealed partial class SyncSettingsJson : JsonSerializerContext;
