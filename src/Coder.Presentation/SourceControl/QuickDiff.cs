using System.Collections.Specialized;
using Coder.Core.Diff;
using Coder.Core.Git;
using Coder.Core.Text;
using Coder.Presentation.Editors;

namespace Coder.Presentation.SourceControl;

/// <summary>
/// Keeps each open file's <see cref="EditorTabViewModel.LineChanges"/> current: the file's lines
/// compared with its staged (index) version, which the editor shows as gutter markers. Untracked
/// files have nothing to compare with and get no markers. Diffs run on a background thread a moment
/// after typing stops; the index version is re-read after git operations and refreshes.
/// </summary>
internal sealed class QuickDiff : IDisposable
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(300);

    private readonly EditorGroupViewModel _editors;
    private readonly Func<GitRepository?> _repository;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly Dictionary<EditorTabViewModel, Tracked> _tracked = [];
    private readonly HashSet<EditorTabViewModel> _dirty = [];
    private readonly Timer _timer;

    public QuickDiff(EditorGroupViewModel editors, Func<GitRepository?> repository)
    {
        _editors = editors;
        _repository = repository;
        _timer = new Timer(_ => Post(DiffDirtyTabs));
        editors.Tabs.CollectionChanged += OnTabsChanged;
    }

    /// <summary>Re-reads every open file's index version (after a commit, stage or refresh).</summary>
    public void Reload()
    {
        foreach (var tab in _editors.Tabs.OfType<EditorTabViewModel>())
        {
            _ = LoadOriginalAsync(Track(tab));
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _editors.Tabs.CollectionChanged -= OnTabsChanged;
        foreach (var tracked in _tracked.Values)
        {
            tracked.Tab.Document.Changed -= tracked.OnChanged;
        }
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var tab in e.OldItems?.OfType<EditorTabViewModel>() ?? [])
        {
            if (_tracked.Remove(tab, out var tracked))
            {
                tab.Document.Changed -= tracked.OnChanged;
            }
        }

        foreach (var tab in e.NewItems?.OfType<EditorTabViewModel>() ?? [])
        {
            _ = LoadOriginalAsync(Track(tab));
        }
    }

    private Tracked Track(EditorTabViewModel tab)
    {
        if (!_tracked.TryGetValue(tab, out var tracked))
        {
            tracked = new Tracked(tab, () => Schedule(tab));
            _tracked[tab] = tracked;
            tab.Document.Changed += tracked.OnChanged;
        }

        return tracked;
    }

    private async Task LoadOriginalAsync(Tracked tracked)
    {
        var tab = tracked.Tab;
        var repository = _repository();
        string? original = null;
        if (repository is not null && tab.FilePath is { } path && IsInside(repository.Root, path))
        {
            original = await repository.ShowFileAsync(path, revision: string.Empty);
        }

        tracked.OriginalLines = original is null ? null : LineDiff.SplitLines(original);
        Schedule(tab, immediately: true);
    }

    private void Schedule(EditorTabViewModel tab, bool immediately = false)
    {
        _dirty.Add(tab);
        _timer.Change(immediately ? TimeSpan.Zero : Delay, Timeout.InfiniteTimeSpan);
    }

    private void DiffDirtyTabs()
    {
        foreach (var tab in _dirty)
        {
            if (_tracked.TryGetValue(tab, out var tracked))
            {
                _ = DiffAsync(tracked);
            }
        }

        _dirty.Clear();
    }

    /// <summary>Diffs off the UI thread; a result older than the document's current version is dropped.</summary>
    private static async Task DiffAsync(Tracked tracked)
    {
        var tab = tracked.Tab;
        if (tracked.OriginalLines is not { } original)
        {
            tab.SetLineChanges([]);
            return;
        }

        var version = tab.Document.Version;
        var current = LineDiff.SplitLines(tab.Document.GetText());
        var hunks = await Task.Run(() => LineDiff.Compute(original, current));
        if (tab.Document.Version == version)
        {
            tab.SetLineChanges(hunks, original);
        }
    }

    private static bool IsInside(string root, string path) =>
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

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

    private sealed class Tracked(EditorTabViewModel tab, Action changed)
    {
        public EditorTabViewModel Tab { get; } = tab;

        /// <summary>The index version's lines, or null when there is nothing to compare with.</summary>
        public IReadOnlyList<string>? OriginalLines { get; set; }

        public void OnChanged(object? sender, TextChange change) => changed();
    }
}
