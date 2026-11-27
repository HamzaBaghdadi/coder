using Coder.Core.Emmet;
using Coder.Core.Lsp;
using Coder.Core.Search;
using Coder.Core.Snippets;
using Coder.Presentation.Editors;
using Coder.Presentation.Mvvm;

namespace Coder.Presentation.LanguageFeatures;

/// <summary>A completion item as shown: which characters of its label matched what was typed.</summary>
public sealed record CompletionRow(CompletionItem Item, IReadOnlyList<int> MatchedIndices);

/// <summary>
/// The suggestion list. It asks the language server when the user types a word or a trigger
/// character (or presses Ctrl+Space), then filters and sorts the answer locally as typing continues,
/// and closes when the caret leaves the word. Accepting replaces the typed prefix.
/// <para>In HTML, built-in Emmet items join the server's: the typed abbreviation's expansion first,
/// then the snippet names it starts (<c>inp</c> → <c>input:checkbox</c>…), and Tab expands a valid
/// abbreviation when the list is closed.</para>
/// <para>Snippets from installed extensions join them too, by prefix, in every language they are for
/// (not after a trigger character such as <c>.</c>, where only members make sense).</para>
/// </summary>
public sealed class CompletionViewModel : ObservableObject, IDisposable
{
    private const int MaxRows = 200;

    /// <summary>How much text before the caret Emmet looks at to tell element content from a tag or script.</summary>
    private const int EmmetContextLength = 64 * 1024;

    private readonly LanguageServices _languages;
    private readonly EditorGroupViewModel _editors;
    private readonly SnippetCatalog? _snippets;
    private readonly Func<string?> _workspaceFolder;
    private EditorTabViewModel? _tab;
    private IReadOnlyList<CompletionItem> _serverItems = [];
    private IReadOnlyList<CompletionItem> _snippetItems = [];
    private (IReadOnlyList<SnippetDefinition> All, string Key, IReadOnlyList<CompletionItem> Items)? _snippetCache;
    private IReadOnlyList<CompletionItem> _items = [];
    private CompletionItem? _emmetAbbreviation;
    private IReadOnlyList<CompletionRow> _rows = [];
    private int _selectedIndex;
    private bool _isOpen;
    private bool _isIncomplete;
    private int _wordStart;
    private string _filter = string.Empty;
    private CancellationTokenSource? _request;

    /// <param name="snippets">Snippets from installed extensions; null for none.</param>
    /// <param name="workspaceFolder">The open folder, for snippet variables such as <c>WORKSPACE_NAME</c>.</param>
    public CompletionViewModel(LanguageServices languages, EditorGroupViewModel editors, SnippetCatalog? snippets = null, Func<string?>? workspaceFolder = null)
    {
        _languages = languages;
        _editors = editors;
        _snippets = snippets;
        _workspaceFolder = workspaceFolder ?? (() => null);
        editors.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorGroupViewModel.ActiveEditor))
            {
                Close();
            }
        };
    }

    /// <summary>Raised whenever the rows, the selection or the open state change.</summary>
    public event EventHandler? Changed;

    public bool IsOpen
    {
        get => _isOpen;
        private set => SetProperty(ref _isOpen, value);
    }

    public IReadOnlyList<CompletionRow> Rows => _rows;

    public int SelectedIndex => _selectedIndex;

    /// <summary>The highlighted item, for the details card beside the list.</summary>
    public CompletionItem? SelectedItem => IsOpen && _selectedIndex < _rows.Count ? _rows[_selectedIndex].Item : null;

    /// <summary>Where the word being completed starts; the list is placed under it.</summary>
    public int AnchorOffset => _wordStart;

    // Triggers ----------------------------------------------------------------------------------

    /// <summary>Called after typed text was inserted into the active editor.</summary>
    public void OnTextTyped(string text)
    {
        if (_editors.ActiveEditor is not { } tab || text.Length == 0 || !(_languages.CanComplete(tab) || SupportsEmmet(tab) || HasSnippets(tab)))
        {
            return;
        }

        var caret = tab.Editor.Selection.Active;
        var trigger = !_languages.CanComplete(tab) ? null : _languages.CompletionTriggerCharacters(tab).FirstOrDefault(t => EndsWith(tab, caret, t));
        if (trigger is not null)
        {
            _ = RequestAsync(tab, trigger);
        }
        else if (!IsOpen && IsWordChar(tab, text[^1]) && caret - WordStart(tab, caret) >= 1)
        {
            _ = RequestAsync(tab, triggerCharacter: null);
        }
    }

    /// <summary>Ctrl+Space: suggestions for the word at the caret, even an empty one.</summary>
    public void Trigger()
    {
        if (_editors.ActiveEditor is { } tab)
        {
            _ = RequestAsync(tab, triggerCharacter: null);
        }
    }

    // Navigation --------------------------------------------------------------------------------

    /// <summary>Moves the selection; wraps around at either end, like VS Code.</summary>
    public void MoveSelection(int delta)
    {
        if (_rows.Count == 0)
        {
            return;
        }

        var target = _selectedIndex + delta;
        _selectedIndex = Math.Abs(delta) == 1
            ? (target + _rows.Count) % _rows.Count
            : Math.Clamp(target, 0, _rows.Count - 1); // paging stops at the ends
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Select(int index)
    {
        if (index >= 0 && index < _rows.Count)
        {
            _selectedIndex = index;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Inserts the selected item, replacing what was typed of it.</summary>
    public void Accept()
    {
        if (!IsOpen || _tab is not { } tab || _selectedIndex >= _rows.Count)
        {
            return;
        }

        Insert(tab, _rows[_selectedIndex].Item);
    }

    /// <summary>
    /// Tab with the list closed: expands the Emmet abbreviation before the caret (<c>ul>li*3</c>), like VS
    /// Code's <c>emmet.triggerExpansionOnTab</c>. Returns false, leaving Tab to indent, when there is none.
    /// </summary>
    public bool TryExpandAbbreviation()
    {
        if (IsOpen || _editors.ActiveEditor is not { } tab || !SupportsEmmet(tab) || tab.Editor.IsCaretOnSnippetStop || !tab.Editor.Selection.IsEmpty)
        {
            return false;
        }

        var caret = tab.Editor.Selection.Active;
        if (caret < tab.Document.Length && IsWordChar(tab, tab.Document[caret]))
        {
            return false; // in the middle of a word
        }

        if (Emmet(tab, caret).Abbreviation is not { } item)
        {
            return false;
        }

        Insert(tab, item);
        return true;
    }

    private void Insert(EditorTabViewModel tab, CompletionItem item)
    {
        var document = tab.Document;
        var caret = tab.Editor.Selection.Start;
        var start = ReplaceStart(item, tab);
        var text = item.TextEdit?.NewText ?? item.InsertText ?? item.Label;
        var expansion = item.IsSnippet ? Snippet.Expand(text, Variables(tab, start, caret)) : new Snippet.Expansion(text, text.Length, 0);
        var indent = LineIndent(tab, caret);
        int Indented(int offset) => Shift(expansion.Text, offset, indent);
        var indented = indent.Length == 0 ? expansion.Text : expansion.Text.Replace("\n", "\n" + indent, StringComparison.Ordinal);
        var tabStops = item.IsSnippet
            ? expansion.TabStops.Select(s => (s.Number, Indented(s.Start), Indented(s.Start + s.Length) - Indented(s.Start))).ToList()
            : null;
        var additional = item.AdditionalTextEdits
            .Select(e => (document.GetOffset(e.Range.Start), document.GetOffset(e.Range.End), e.NewText))
            .ToList();

        Close();
        var selectionStart = Indented(expansion.SelectionStart);
        tab.Editor.InsertCompletion(start, indented, selectionStart, Indented(expansion.SelectionStart + expansion.SelectionLength) - selectionStart, additional, tabStops);
    }

    public void Dispose() => Close();

    public void Close()
    {
        _request?.Cancel();
        _request = null;
        Detach();
        _items = [];
        _serverItems = [];
        _snippetItems = [];
        _emmetAbbreviation = null;
        _rows = [];
        if (IsOpen)
        {
            IsOpen = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    // Requests and filtering --------------------------------------------------------------------

    private async Task RequestAsync(EditorTabViewModel tab, string? triggerCharacter)
    {
        _request?.Cancel();
        using var request = _request = new CancellationTokenSource();
        var caret = tab.Editor.Selection.Active;
        var wordStart = WordStart(tab, caret);

        var list = _languages.CanComplete(tab) ? await _languages.CompleteAsync(tab, caret, triggerCharacter, request.Token) : null;
        if (!ReferenceEquals(_request, request))
        {
            return; // superseded by a newer request, or closed
        }

        _request = null;
        var snippets = triggerCharacter is null ? SnippetItems(tab) : [];
        if ((list is null && !SupportsEmmet(tab) && snippets.Count == 0) || !ReferenceEquals(tab, _editors.ActiveEditor))
        {
            return;
        }

        Detach();
        _tab = tab;
        _wordStart = wordStart;
        _serverItems = list?.Items ?? [];
        _snippetItems = snippets;
        _isIncomplete = list?.IsIncomplete ?? false;
        _filter = string.Empty;
        tab.Editor.SelectionChanged += OnSelectionChanged;
        Refilter();
    }

    private void OnSelectionChanged(object? sender, EventArgs e) => Refilter();

    /// <summary>Filters by the text between the word start and the caret; closes when the caret left the word.</summary>
    private void Refilter()
    {
        if (_tab is not { } tab)
        {
            return;
        }

        var editor = tab.Editor;
        var caret = editor.Selection.Active;
        var leftWord = caret < _wordStart || (caret != _wordStart && WordStart(tab, caret) != _wordStart);
        if (leftWord || !editor.Selection.IsEmpty)
        {
            Close();
            return;
        }

        var typed = tab.Document.GetText(_wordStart, caret - _wordStart);
        if (_isIncomplete && typed != _filter && _request is null && typed.Length > _filter.Length)
        {
            _ = RequestAsync(tab, triggerCharacter: null); // the server only sent part of the list
        }

        _filter = typed;
        var emmet = Emmet(tab, caret);
        _emmetAbbreviation = emmet.Abbreviation;
        _items = emmet.Items.Count == 0 && _snippetItems.Count == 0 ? _serverItems : [.. emmet.Items, .. _snippetItems, .. _serverItems];
        _rows = Filter(_items, tab, caret);
        _selectedIndex = typed.Length == 0 && _rows.ToList().FindIndex(r => r.Item.Preselect) is var preselected and >= 0 ? preselected : 0;
        IsOpen = _rows.Count > 0;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private IReadOnlyList<CompletionRow> Filter(IReadOnlyList<CompletionItem> items, EditorTabViewModel tab, int caret)
    {
        var scored = new List<(CompletionItem Item, int Score, string Typed)>();
        foreach (var item in items)
        {
            var start = ReplaceStart(item, tab);
            var typed = start <= caret ? tab.Document.GetText(start, caret - start) : string.Empty;
            if (FuzzyMatcher.Score(typed, item.FilterKey) is { } score)
            {
                scored.Add((item, score, typed));
            }
        }

        // Emmet's snippet names all start with what was typed: keep them together in alphabetical order
        // (input, input:b, input:btn…) as VS Code lists them, rather than shortest first.
        static bool IsEmmetName(CompletionItem item) => item.Detail == EmmetEngine.ItemDetail;
        if (scored.Any(s => IsEmmetName(s.Item)))
        {
            var emmetScore = scored.Where(s => IsEmmetName(s.Item)).Max(s => s.Score);
            scored = scored.Select(s => IsEmmetName(s.Item) ? s with { Score = emmetScore } : s).ToList();
        }

        var ordered = _filter.Length == 0
            ? scored.OrderBy(s => s.Item.SortKey, StringComparer.Ordinal)
            : scored.OrderByDescending(s => s.Score).ThenBy(s => s.Item.SortKey, StringComparer.Ordinal);
        // A valid Emmet abbreviation leads, as in VS Code: what was typed is what most likely is wanted.
        return ordered
            .OrderBy(s => ReferenceEquals(s.Item, _emmetAbbreviation) ? 0 : 1) // a stable sort: the rest keep their order
            .Take(MaxRows)
            .Select(s => new CompletionRow(s.Item, FuzzyMatcher.MatchedIndices(s.Typed, s.Item.Label) ?? []))
            .ToList();
    }

    /// <summary>Where an item's text starts: its own edit range when the server gave one, else the word start.</summary>
    private int ReplaceStart(CompletionItem item, EditorTabViewModel tab) =>
        item.TextEdit is { } edit ? Math.Min(tab.Document.GetOffset(edit.Range.Start), tab.Editor.Selection.Start) : _wordStart;

    private void Detach()
    {
        if (_tab is not null)
        {
            _tab.Editor.SelectionChanged -= OnSelectionChanged;
            _tab = null;
        }
    }

    // Emmet ---------------------------------------------------------------------------------------

    /// <summary>Emmet works in HTML files, where text typed outside tags is element content.</summary>
    private static bool SupportsEmmet(EditorTabViewModel tab) => tab.Language.Id == "html";

    private static EmmetCompletions Emmet(EditorTabViewModel tab, int caret)
    {
        if (!SupportsEmmet(tab))
        {
            return EmmetCompletions.None;
        }

        var start = Math.Max(0, caret - EmmetContextLength);
        var options = tab.Editor.Options;
        var indent = options.InsertSpaces ? new string(' ', options.TabSize) : "\t";
        return EmmetEngine.Complete(tab.Document.GetText(start, caret - start), tab.Document.GetPosition(caret), new EmmetOptions(indent));
    }

    // Snippets ------------------------------------------------------------------------------------

    private bool HasSnippets(EditorTabViewModel tab) => _snippets is not null && SnippetItems(tab).Count > 0;

    /// <summary>One item per prefix of each snippet for the file's language; the card shows what it inserts.</summary>
    private IReadOnlyList<CompletionItem> SnippetItems(EditorTabViewModel tab)
    {
        if (_snippets is null)
        {
            return [];
        }

        var languages = SnippetLanguages.For(tab.Language.Id, tab.FilePath);
        var key = string.Join(",", languages);
        var all = _snippets.All; // a new list after an extension is installed or uninstalled
        if (_snippetCache is { } cache && ReferenceEquals(cache.All, all) && cache.Key == key)
        {
            return cache.Items;
        }

        var items = new List<CompletionItem>();
        foreach (var snippet in all.Where(s => s.AppliesTo(languages)))
        {
            var preview = Snippet.Expand(snippet.Body).Text;
            var documentation = snippet.Description is { Length: > 0 } description ? $"{description}\n\n{preview}" : preview;
            items.AddRange(snippet.Prefixes.Select(prefix => new CompletionItem
            {
                Label = prefix,
                Kind = CompletionItemKind.Snippet,
                Detail = snippet.Name,
                Documentation = documentation,
                InsertText = snippet.Body,
                IsSnippet = true,
            }));
        }

        _snippetCache = (all, key, items);
        return items;
    }

    /// <summary>Snippet variables (<c>TM_FILENAME_BASE</c>, <c>CURRENT_YEAR</c>...) for an insertion replacing start..caret.</summary>
    private Func<string, string?> Variables(EditorTabViewModel tab, int start, int caret)
    {
        var document = tab.Document;
        var line = document.GetLineOfOffset(caret);
        var selection = tab.Editor.Selection;
        var context = new SnippetContext(
            tab.FilePath,
            _workspaceFolder(),
            document.GetLineText(line),
            line,
            start <= caret ? document.GetText(start, caret - start) : string.Empty,
            selection.IsEmpty ? string.Empty : document.GetText(selection.Start, selection.End - selection.Start),
            tab.Language.LineComment,
            tab.Language.BlockComment,
            DateTime.Now);
        return name => SnippetVariables.Resolve(name, context);
    }

    // Text helpers ------------------------------------------------------------------------------

    private static bool IsWordChar(EditorTabViewModel tab, char c) =>
        char.IsLetterOrDigit(c) || tab.Language.ExtraIdentifierChars.Contains(c, StringComparison.Ordinal);

    private static int WordStart(EditorTabViewModel tab, int caret)
    {
        var document = tab.Document;
        var lineStart = document.GetLineStart(document.GetLineOfOffset(caret));
        var start = caret;
        while (start > lineStart && IsWordChar(tab, document[start - 1]))
        {
            start--;
        }

        return start;
    }

    private static bool EndsWith(EditorTabViewModel tab, int caret, string text) =>
        caret >= text.Length && tab.Document.GetText(caret - text.Length, text.Length) == text;

    private static string LineIndent(EditorTabViewModel tab, int offset)
    {
        var line = tab.Document.GetLineText(tab.Document.GetLineOfOffset(offset));
        return line[..(line.Length - line.TrimStart(' ', '\t').Length)];
    }

    /// <summary>
    /// Multi-line text continues at the current line's indentation, as if typed there: where an offset in
    /// the text ends up once every line break is followed by <paramref name="indent"/>.
    /// </summary>
    private static int Shift(string text, int offset, string indent) =>
        offset + text.AsSpan(0, Math.Min(offset, text.Length)).Count('\n') * indent.Length;
}
