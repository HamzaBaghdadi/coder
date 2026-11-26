using Coder.Presentation.Mvvm;

namespace Coder.Presentation.Updates;

/// <summary>Finds, downloads and applies new versions of Coder; implemented by the app (Velopack, GitHub Releases).</summary>
public interface IUpdateService
{
    /// <summary>False for a build run from source: updates only work in the installed app.</summary>
    bool IsInstalled { get; }

    /// <summary>The running version ("0.1.0").</summary>
    string CurrentVersion { get; }

    /// <summary>The newer version available, or null when this one is the latest.</summary>
    Task<string?> CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>Downloads the version <see cref="CheckAsync"/> found (only the changes when it can).</summary>
    Task DownloadAsync(Action<int> progress, CancellationToken cancellationToken = default);

    /// <summary>Installs the downloaded version and starts it again.</summary>
    void ApplyAndRestart();

    /// <summary>Installs the downloaded version once Coder exits, without starting it (the next start is up to date).</summary>
    void ApplyOnExit();
}

public enum UpdateState
{
    Idle,
    Checking,
    Downloading,
    /// <summary>Downloaded: restart to use it (or it is installed when Coder closes).</summary>
    Ready,
    UpToDate,
    Failed,
    /// <summary>Run from source: there is nothing to update.</summary>
    NotInstalled,
}

/// <summary>
/// Keeps Coder current without getting in the way: it checks shortly after start and every few hours,
/// downloads new versions in the background, then offers "Restart to Update" (the status bar and the
/// gear menu). Closing Coder installs it too, so the next start is up to date either way.
/// </summary>
public sealed class UpdatesViewModel : ObservableObject, IDisposable
{
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(4);

    private readonly IUpdateService _service;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly CancellationTokenSource _stop = new();
    private UpdateState _state;
    private string? _version;
    private int _progress;
    private string _error = string.Empty;
    private Task? _busy;
    private bool _restarting;
    private bool _disposed;

    public UpdatesViewModel(IUpdateService service)
    {
        _service = service;
        _state = service.IsInstalled ? UpdateState.Idle : UpdateState.NotInstalled;
    }

    /// <summary>Asks before a restart: returns false when the user cancelled (unsaved files).</summary>
    public Func<Task<bool>> ConfirmRestart { get; set; } = () => Task.FromResult(true);

    public string CurrentVersion => _service.CurrentVersion;

    public UpdateState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsReady));
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public bool IsReady => State == UpdateState.Ready;

    /// <summary>The version being downloaded or ready.</summary>
    public string? Version
    {
        get => _version;
        private set => SetProperty(ref _version, value);
    }

    public int Progress
    {
        get => _progress;
        private set
        {
            if (SetProperty(ref _progress, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    /// <summary>One line for the status bar and the menu: "Restart to update to 0.2.0", "Downloading update… 40%".</summary>
    public string StatusText => State switch
    {
        UpdateState.Checking => "Checking for updates…",
        UpdateState.Downloading => $"Downloading Coder {Version}… {Progress}%",
        UpdateState.Ready => $"Restart to update to {Version}",
        UpdateState.UpToDate => $"Coder {CurrentVersion} is the latest version.",
        UpdateState.Failed => $"Couldn't check for updates: {_error}",
        UpdateState.NotInstalled => "Updates work in the installed app (this build runs from source).",
        _ => string.Empty,
    };

    /// <summary>Checks soon after start, then every few hours, for as long as Coder runs.</summary>
    public void StartBackgroundChecks()
    {
        if (!_service.IsInstalled)
        {
            return;
        }

        _ = LoopAsync();
    }

    /// <summary>"Check for Updates…": checks now and says what it found.</summary>
    public Task CheckNowAsync() => RunAsync(manual: true);

    /// <summary>"Restart to Update": installs the downloaded version and starts it, after unsaved files are dealt with.</summary>
    public async Task RestartToUpdateAsync()
    {
        if (State != UpdateState.Ready)
        {
            return;
        }

        _restarting = true; // closing the workbench now must not also schedule the install-on-exit
        if (!await ConfirmRestart())
        {
            _restarting = false;
            return;
        }

        _service.ApplyAndRestart();
    }

    /// <summary>Coder is closing: a downloaded update is installed after it exits.</summary>
    public void OnExit()
    {
        if (State == UpdateState.Ready && !_restarting)
        {
            _service.ApplyOnExit();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return; // closing and tests may both dispose the workbench
        }

        _disposed = true;
        _stop.Cancel();
        _stop.Dispose();
    }

    private async Task LoopAsync()
    {
        try
        {
            await Task.Delay(FirstCheckDelay, _stop.Token);
            while (!_disposed)
            {
                await RunAsync(manual: false);
                await Task.Delay(CheckInterval, _stop.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Coder is closing
        }
    }

    private Task RunAsync(bool manual)
    {
        if (!_service.IsInstalled)
        {
            State = UpdateState.NotInstalled;
            return Task.CompletedTask;
        }

        if (_busy is { IsCompleted: false } || State == UpdateState.Ready)
        {
            return _busy ?? Task.CompletedTask; // already on it, or waiting for a restart
        }

        return _busy = CheckAndDownloadAsync(manual);
    }

    private async Task CheckAndDownloadAsync(bool manual)
    {
        var quiet = !manual;
        try
        {
            if (manual)
            {
                State = UpdateState.Checking;
            }

            var version = await _service.CheckAsync(_stop.Token);
            if (version is null)
            {
                State = manual ? UpdateState.UpToDate : UpdateState.Idle;
                return;
            }

            Version = version;
            Progress = 0;
            State = UpdateState.Downloading;
            await _service.DownloadAsync(percent => Post(() => Progress = percent), _stop.Token);
            State = UpdateState.Ready;
        }
        catch (OperationCanceledException)
        {
            // closing
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Offline or GitHub unreachable: a background check stays silent and tries again later.
            _error = exception.Message;
            State = quiet ? UpdateState.Idle : UpdateState.Failed;
        }
    }

    private void Post(Action action)
    {
        if (_context is null)
        {
            action();
        }
        else
        {
            _context.Post(_ => action(), null);
        }
    }
}

/// <summary>For builds without an updater (tests, running from source).</summary>
public sealed class NoUpdates : IUpdateService
{
    public bool IsInstalled => false;

    public string CurrentVersion => typeof(NoUpdates).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public Task<string?> CheckAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

    public Task DownloadAsync(Action<int> progress, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void ApplyAndRestart()
    {
    }

    public void ApplyOnExit()
    {
    }
}
