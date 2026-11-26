using Coder.Core.Git;
using Coder.Presentation.Editors;
using Coder.Presentation.Mvvm;

namespace Coder.Presentation.SourceControl;

/// <summary>
/// Who last changed the line under the caret (git blame), like VS Code: faint text at the end of the line
/// ("You, 2 days ago • Fix the parser") and the author and age in the status bar. Blame runs on the
/// editor's text, unsaved edits included, a moment after typing stops; until then the annotation hides,
/// so it never names the wrong line.
/// </summary>
public sealed class BlameViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(400);

    private readonly EditorGroupViewModel _editors;
    private readonly Func<GitRepository?> _repository;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly Timer _timer;
    private EditorTabViewModel? _tab;
    private IReadOnlyList<GitBlameLine> _lines = [];
    private EditorTabViewModel? _blamedTab;
    private int _blamedVersion = -1;
    private GitRepository? _emailRepository;
    private string? _userEmail;
    private string _statusText = string.Empty;
    private string _statusDetail = string.Empty;
    private bool _isInlineEnabled = true;
    private int _generation;

    public BlameViewModel(EditorGroupViewModel editors, Func<GitRepository?> repository)
    {
        _editors = editors;
        _repository = repository;
        _timer = new Timer(_ => Post(() => _ = BlameAsync()));
        editors.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorGroupViewModel.ActiveTab))
            {
                Follow(editors.ActiveEditor);
            }
        };
        Follow(editors.ActiveEditor);
    }

    /// <summary>The blame changed or became stale: views showing it repaint.</summary>
    public event EventHandler? Changed;

    /// <summary>Whether the editor shows blame at the end of the caret's line.</summary>
    public bool IsInlineEnabled
    {
        get => _isInlineEnabled;
        set
        {
            if (SetProperty(ref _isInlineEnabled, value))
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>The caret line's author and age for the status bar ("You, 2 days ago"); empty when unknown.</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>The caret line's commit in full, for the status bar's tooltip.</summary>
    public string StatusDetail
    {
        get => _statusDetail;
        private set => SetProperty(ref _statusDetail, value);
    }

    /// <summary>The faint text after <paramref name="line"/> of <paramref name="tab"/>, or null (unknown, stale or turned off).</summary>
    public string? Annotation(EditorTabViewModel tab, int line)
    {
        if (!IsInlineEnabled || LineOf(tab, line) is not { } blame)
        {
            return null;
        }

        return blame.IsCommitted ? $"{Who(blame)}, {When(blame)} • {blame.Summary}" : "You • Uncommitted changes";
    }

    /// <summary>Blames the active file again (after a commit, checkout or pull).</summary>
    public void Refresh() => Schedule();

    public void Dispose() => _timer.Dispose();

    private GitBlameLine? LineOf(EditorTabViewModel tab, int line) =>
        ReferenceEquals(tab, _blamedTab) && tab.Document.Version == _blamedVersion && line >= 0 && line < _lines.Count ? _lines[line] : null;

    private void Follow(EditorTabViewModel? tab)
    {
        if (ReferenceEquals(tab, _tab))
        {
            return;
        }

        if (_tab is not null)
        {
            _tab.Document.Changed -= OnDocumentChanged;
            _tab.Editor.SelectionChanged -= OnSelectionChanged;
        }

        _tab = tab;
        if (tab is not null)
        {
            tab.Document.Changed += OnDocumentChanged;
            tab.Editor.SelectionChanged += OnSelectionChanged;
        }

        UpdateStatus();
        Schedule();
    }

    /// <remarks>
    /// Runs in the middle of an edit, before the caret has moved, so nothing here may look at the caret
    /// (it can point past the end of the shortened text). The blame is stale now anyway: clear it.
    /// </remarks>
    private void OnDocumentChanged(object? sender, Coder.Core.Text.TextChange e)
    {
        Schedule();
        StatusText = string.Empty;
        StatusDetail = string.Empty;
    }

    private void OnSelectionChanged(object? sender, EventArgs e) => UpdateStatus();

    private void Schedule()
    {
        _generation++;
        _timer.Change(Delay, Timeout.InfiniteTimeSpan);
    }

    private async Task BlameAsync()
    {
        var generation = _generation;
        if (_tab is not { FilePath: { } path } tab || _repository() is not { } repository)
        {
            Store(null, -1, []);
            return;
        }

        var version = tab.Document.Version;
        var lines = await repository.BlameAsync(path, tab.Document.GetTextForSave());
        if (!ReferenceEquals(repository, _emailRepository))
        {
            _userEmail = await repository.UserEmailAsync();
            _emailRepository = repository;
        }

        if (generation == _generation && ReferenceEquals(tab, _tab) && version == tab.Document.Version)
        {
            Store(tab, version, lines);
        }
    }

    private void Store(EditorTabViewModel? tab, int version, IReadOnlyList<GitBlameLine> lines)
    {
        (_blamedTab, _blamedVersion, _lines) = (tab, version, lines);
        UpdateStatus();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateStatus()
    {
        if (_tab is not { } tab || LineOf(tab, tab.Document.GetLineOfOffset(Math.Min(tab.Editor.Selection.Active, tab.Document.Length))) is not { } blame)
        {
            StatusText = string.Empty;
            StatusDetail = string.Empty;
            return;
        }

        if (!blame.IsCommitted)
        {
            StatusText = "You, uncommitted";
            StatusDetail = "This line has changed since the last commit.";
            return;
        }

        StatusText = $"{Who(blame)}, {When(blame)}";
        StatusDetail = $"{blame.Summary}\n{blame.Author} <{blame.Email}>, {RelativeTime.Exact(blame.Date)}\nCommit {blame.ShortHash}";
    }

    private string Who(GitBlameLine blame) =>
        _userEmail is { Length: > 0 } email && string.Equals(blame.Email, email, StringComparison.OrdinalIgnoreCase) ? "You" : blame.Author;

    private static string When(GitBlameLine blame) => RelativeTime.Format(blame.Date, DateTimeOffset.Now);

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
