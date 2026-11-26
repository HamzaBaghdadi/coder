using Coder.Core.Search;
using Coder.Core.Text;
using Coder.Presentation.Mvvm;
using Coder.Presentation.Search;

namespace Coder.Presentation.Editors;

/// <summary>
/// Find and replace for the active editor. Matches are recomputed when the query, an option, the tab
/// or the text changes, and exposed so the editor can highlight them. Text changes are batched: the
/// search reruns once per edit operation, not once per individual change.
/// </summary>
public sealed class FindViewModel : ObservableObject, ISearchOptions
{
    private readonly EditorGroupViewModel _editors;
    private EditorTabViewModel? _tab;
    private bool _isStale;

    private bool _isOpen;
    private bool _isReplaceVisible;
    private string _query = string.Empty;
    private string _replacement = string.Empty;
    private bool _matchCase;
    private bool _wholeWord;
    private bool _useRegex;
    private SearchResult _result = SearchResult.Empty;
    private int _currentIndex = -1;

    public FindViewModel(EditorGroupViewModel editors)
    {
        _editors = editors;
        editors.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorGroupViewModel.ActiveEditor))
            {
                Track(editors.ActiveEditor);
            }
        };
    }

    /// <summary>Asks the view to focus the query field (raised on every open, even if already open).</summary>
    public event EventHandler? FocusRequested;

    /// <summary>Raised when the widget closes, so focus can go back to the text.</summary>
    public event EventHandler? Closed;

    public bool IsOpen
    {
        get => _isOpen;
        private set => SetProperty(ref _isOpen, value);
    }

    public bool IsReplaceVisible
    {
        get => _isReplaceVisible;
        set => SetProperty(ref _isReplaceVisible, value);
    }

    public string Query
    {
        get => _query;
        set
        {
            if (SetProperty(ref _query, value))
            {
                Search(revealFromCaret: true);
            }
        }
    }

    public string Replacement
    {
        get => _replacement;
        set => SetProperty(ref _replacement, value);
    }

    public bool MatchCase
    {
        get => _matchCase;
        set => SetOption(ref _matchCase, value);
    }

    public bool WholeWord
    {
        get => _wholeWord;
        set => SetOption(ref _wholeWord, value);
    }

    public bool UseRegex
    {
        get => _useRegex;
        set => SetOption(ref _useRegex, value);
    }

    /// <summary>Matches to highlight; empty while closed.</summary>
    public IReadOnlyList<SearchMatch> Matches => IsOpen ? _result.Matches : [];

    public int CurrentIndex
    {
        get => _currentIndex;
        private set
        {
            if (SetProperty(ref _currentIndex, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public bool HasError => _result.Error is not null;

    /// <summary>"3 of 12", "No results", or the regex error.</summary>
    public string StatusText => _result switch
    {
        { Error: { } error } => error,
        _ when Query.Length == 0 => string.Empty,
        { Matches.Count: 0 } => "No results",
        { IsTruncated: true } => $"{Math.Max(CurrentIndex + 1, 1)} of {_result.Matches.Count}+",
        _ => CurrentIndex >= 0 ? $"{CurrentIndex + 1} of {_result.Matches.Count}" : $"{_result.Matches.Count} results",
    };

    private SearchOptions Options => new(MatchCase, WholeWord, UseRegex);

    /// <summary>Ctrl+F / Ctrl+H: opens the widget, seeding the query from a single-line selection.</summary>
    public void Open(bool withReplace)
    {
        if (_tab is { Editor: var editor } && editor.Selection is { IsEmpty: false, Length: < 200 })
        {
            var selected = editor.SelectedText;
            if (!selected.Contains('\n', StringComparison.Ordinal))
            {
                _query = selected;
                OnPropertyChanged(nameof(Query));
            }
        }

        IsReplaceVisible = withReplace || (IsOpen && IsReplaceVisible);
        IsOpen = true;
        Search(revealFromCaret: false);
        FocusRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Close()
    {
        if (!IsOpen)
        {
            return;
        }

        IsOpen = false;
        OnPropertyChanged(nameof(Matches));
        Closed?.Invoke(this, EventArgs.Empty);
    }

    public void FindNext() => Step(forward: true);

    public void FindPrevious() => Step(forward: false);

    /// <summary>Replaces the selected match (if the selection is one) and moves to the next match.</summary>
    public void ReplaceCurrent()
    {
        if (_tab is not { } tab || _result.Matches.Count == 0)
        {
            return;
        }

        var selection = tab.Editor.Selection;
        var current = _result.Matches.FirstOrDefault(m => m.Start == selection.Start && m.Length == selection.Length);
        if (current.Length > 0)
        {
            ReplaceAll([current]);
        }

        FindNext();
    }

    public void ReplaceAll() => ReplaceAll(_result.Matches);

    /// <summary>Alt+Enter: puts a cursor on every match and returns to the editor to type over them.</summary>
    public void SelectAllMatches()
    {
        if (_tab is null || _result.Matches.Count == 0 || HasError)
        {
            return;
        }

        _tab.Editor.SelectAll(_result.Matches, CurrentIndex);
        Close();
    }

    private void ReplaceAll(IReadOnlyList<SearchMatch> matches)
    {
        if (_tab is not { } tab || matches.Count == 0 || HasError)
        {
            return;
        }

        var replacer = TextSearch.CreateReplacer(tab.Document.GetText(), Query, Replacement, Options);
        tab.Editor.ReplaceMatches(matches, replacer);
    }

    private void Step(bool forward)
    {
        if (!IsOpen)
        {
            Open(withReplace: false);
        }

        if (_tab is not { } tab || _result.Matches.Count == 0)
        {
            return;
        }

        var matches = _result.Matches;
        var selection = tab.Editor.Selection;
        var index = forward
            ? IndexOfFirst(matches, m => m.Start >= selection.End)
            : IndexOfLast(matches, m => m.End <= selection.Start);
        if (index < 0)
        {
            index = forward ? 0 : matches.Count - 1; // wrap around
        }

        CurrentIndex = index;
        tab.Editor.Select(matches[index]);
    }

    /// <summary>Reruns the search. When typing a query, the nearest match at or after the caret is selected.</summary>
    private void Search(bool revealFromCaret)
    {
        _isStale = false;
        _result = IsOpen && _tab is not null
            ? TextSearch.FindAll(_tab.Document.GetText(), Query, Options)
            : SearchResult.Empty;

        var caret = _tab?.Editor.Selection.Start ?? 0;
        var index = IndexOfFirst(_result.Matches, m => m.End > caret);
        if (index < 0 && _result.Matches.Count > 0)
        {
            index = 0;
        }

        _currentIndex = index;
        if (revealFromCaret && index >= 0)
        {
            _tab!.Editor.Select(_result.Matches[index]);
        }

        OnPropertyChanged(nameof(Matches));
        OnPropertyChanged(nameof(CurrentIndex));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasError));
    }

    private void SetOption(ref bool field, bool value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (SetProperty(ref field, value, name))
        {
            Search(revealFromCaret: true);
        }
    }

    private void Track(EditorTabViewModel? tab)
    {
        if (_tab is not null)
        {
            _tab.Document.Changed -= OnDocumentChanged;
            _tab.Editor.SelectionChanged -= OnSelectionChanged;
        }

        _tab = tab;
        if (_tab is not null)
        {
            _tab.Document.Changed += OnDocumentChanged;
            _tab.Editor.SelectionChanged += OnSelectionChanged;
        }

        Search(revealFromCaret: false);
    }

    private void OnDocumentChanged(object? sender, TextChange e) => _isStale = true;

    // Every edit operation ends with one selection change, so this reruns the search once per edit.
    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (_isStale && IsOpen)
        {
            Search(revealFromCaret: false);
        }
    }

    private static int IndexOfFirst(IReadOnlyList<SearchMatch> matches, Func<SearchMatch, bool> predicate)
    {
        for (var i = 0; i < matches.Count; i++)
        {
            if (predicate(matches[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static int IndexOfLast(IReadOnlyList<SearchMatch> matches, Func<SearchMatch, bool> predicate)
    {
        for (var i = matches.Count - 1; i >= 0; i--)
        {
            if (predicate(matches[i]))
            {
                return i;
            }
        }

        return -1;
    }
}
