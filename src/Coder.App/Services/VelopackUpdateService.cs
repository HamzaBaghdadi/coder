using Coder.Presentation.Updates;
using Velopack;
using Velopack.Sources;

namespace Coder.App.Services;

/// <summary>
/// Updates from the GitHub Releases of github.com/HamzaBaghdadi/coder, made by tools/release.ps1 with
/// Velopack: only the changed files are downloaded when possible, and an update is installed by
/// Velopack's small updater after Coder exits (then Coder starts again, or not, as asked).
/// </summary>
internal sealed class VelopackUpdateService : IUpdateService
{
    public const string Repository = "https://github.com/HamzaBaghdadi/coder";

    private readonly UpdateManager _manager = new(new GithubSource(Repository, accessToken: null, prerelease: false));
    private UpdateInfo? _found;

    public bool IsInstalled => _manager.IsInstalled;

    public string CurrentVersion => _manager.CurrentVersion?.ToString()
        ?? typeof(VelopackUpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public async Task<string?> CheckAsync(CancellationToken cancellationToken = default)
    {
        _found = await _manager.CheckForUpdatesAsync().WaitAsync(cancellationToken);
        return _found?.TargetFullRelease.Version.ToString();
    }

    public Task DownloadAsync(Action<int> progress, CancellationToken cancellationToken = default) =>
        _found is null ? Task.CompletedTask : _manager.DownloadUpdatesAsync(_found, progress, cancellationToken);

    public void ApplyAndRestart()
    {
        if (_found is not null)
        {
            _manager.ApplyUpdatesAndRestart(_found.TargetFullRelease);
        }
    }

    public void ApplyOnExit()
    {
        if (_found is not null)
        {
            _manager.WaitExitThenApplyUpdates(_found.TargetFullRelease, silent: true, restart: false);
        }
    }
}
