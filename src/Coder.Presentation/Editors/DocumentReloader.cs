using System.Collections.Concurrent;
using Coder.Core.Files;
using Coder.Core.Lsp;

namespace Coder.Presentation.Editors;

/// <summary>
/// Watches the project's files. Open documents stay in step with their files: when a file changes on
/// disk (git discard, checkout, pull, a formatter, another program), its tab shows the new content; tabs
/// with unsaved changes keep them (see <see cref="EditorTabViewModel.ReloadFromDisk"/>). Every change is
/// also reported in batches through <see cref="FilesChanged"/>, so language servers learn about files
/// changed behind their back.
/// </summary>
public sealed class DocumentReloader : IDisposable
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(100);

    /// <summary>Folders whose churn no one needs to hear about (git's own files, dependencies, build output).</summary>
    private static readonly string[] IgnoredFolders = [".git", "node_modules", "bin", "obj", ".vs"];

    private readonly IFileSystem _fileSystem;
    private readonly EditorGroupViewModel _editors;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly ConcurrentDictionary<string, FileChangeType> _changed = new(DocumentUri.PathComparer);
    private readonly Timer _timer;
    private FileSystemWatcher? _watcher;
    private string? _root;

    public DocumentReloader(IFileSystem fileSystem, EditorGroupViewModel editors)
    {
        _fileSystem = fileSystem;
        _editors = editors;
        _timer = new Timer(_ => Post(() => _ = OnBatchAsync(TakeChanged())));
    }

    /// <summary>Files created, changed or deleted on disk, a moment after they settle (on the UI thread).</summary>
    public event EventHandler<IReadOnlyList<(string Path, FileChangeType Type)>>? FilesChanged;

    /// <summary>Watches the files under <paramref name="folder"/> (the open project); null stops watching.</summary>
    public void Watch(string? folder)
    {
        _watcher?.Dispose();
        _watcher = null;
        _root = folder;
        if (folder is null || !Directory.Exists(folder))
        {
            return;
        }

        try
        {
            _watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            _watcher.Changed += (_, e) => OnChanged(e.FullPath, FileChangeType.Changed);
            _watcher.Created += (_, e) => OnChanged(e.FullPath, FileChangeType.Created);
            _watcher.Deleted += (_, e) => OnChanged(e.FullPath, FileChangeType.Deleted);
            _watcher.Renamed += (_, e) =>
            {
                // Tools that save through a temporary file rename it over the real one.
                OnChanged(e.OldFullPath, FileChangeType.Deleted);
                OnChanged(e.FullPath, FileChangeType.Created);
            };
            _watcher.Error += (_, _) => Post(() => _ = ReloadAllAsync()); // events were lost: check everything
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            _watcher = null; // explicit reloads (after git operations) still work
        }
    }

    /// <summary>Checks every open document against its file, for when files may have changed (after a git operation).</summary>
    public Task ReloadAllAsync() =>
        ReloadAsync(_editors.Tabs.OfType<EditorTabViewModel>().Select(tab => tab.FilePath).OfType<string>().ToList());

    public void Dispose()
    {
        _watcher?.Dispose();
        _timer.Dispose();
    }

    private void OnChanged(string path, FileChangeType type)
    {
        // A file created and then written in the same batch is still new to whoever hears about it.
        _changed.AddOrUpdate(path, type, (_, previous) => previous == FileChangeType.Created && type == FileChangeType.Changed ? previous : type);
        _timer.Change(Delay, Timeout.InfiniteTimeSpan); // a write often comes as several events
    }

    private List<(string Path, FileChangeType Type)> TakeChanged()
    {
        var changes = new List<(string, FileChangeType)>();
        foreach (var path in _changed.Keys.ToList())
        {
            if (_changed.TryRemove(path, out var type))
            {
                changes.Add((path, type));
            }
        }

        return changes;
    }

    private async Task OnBatchAsync(List<(string Path, FileChangeType Type)> changes)
    {
        await ReloadAsync(changes.Where(c => c.Type != FileChangeType.Deleted).Select(c => c.Path).ToList());
        var relevant = changes.Where(c => !IsIgnored(c.Path)).ToList();
        if (relevant.Count > 0)
        {
            FilesChanged?.Invoke(this, relevant);
        }
    }

    private bool IsIgnored(string path)
    {
        if (path.EndsWith(".coder-save", StringComparison.Ordinal))
        {
            return true; // Coder's own save-in-progress file
        }

        var relative = _root is null ? path : Path.GetRelativePath(_root, path);
        return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).SkipLast(1).Any(folder => IgnoredFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
               || IgnoredFolders.Contains(relative, StringComparer.OrdinalIgnoreCase);
    }

    private async Task ReloadAsync(IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            if (_editors.FindTab(path) is not { IsDirty: false } tab || !File.Exists(path))
            {
                continue; // not open, has unsaved work, or was deleted (the tab keeps its text)
            }

            var version = tab.Document.Version;
            string text;
            try
            {
                text = (await _fileSystem.ReadTextAsync(path)).Text;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue; // still being written: the write's next event retries
            }

            // The user may have typed or saved while the file was read; their version wins.
            if (tab.Document.Version == version && _editors.FindTab(path) == tab)
            {
                tab.ReloadFromDisk(text);
            }
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
