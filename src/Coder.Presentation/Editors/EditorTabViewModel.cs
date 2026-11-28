using System.Text;
using Coder.Core.Diff;
using Coder.Core.Editing;
using Coder.Core.Git;
using Coder.Core.Lsp;
using Coder.Core.Syntax;
using Coder.Core.Text;
using Coder.Presentation.Mvvm;

namespace Coder.Presentation.Editors;

/// <summary>One open document: its editing model, highlighter and file identity.</summary>
public sealed class EditorTabViewModel : TabViewModel
{
    private static int s_untitledCounter;

    private string? _filePath;
    private IReadOnlyList<DiagnosticMarker> _diagnostics = [];
    private IReadOnlyList<DiffHunk> _lineChanges = [];

    public EditorTabViewModel(string? filePath, string text, Encoding encoding)
    {
        _filePath = filePath;
        Encoding = encoding;
        UntitledName = filePath is null ? $"Untitled-{Interlocked.Increment(ref s_untitledCounter)}" : string.Empty;
        Language = filePath is null ? Languages.PlainText : Languages.FromFileName(filePath);

        Document = new TextDocument(text);
        Editor = new EditorState(Document, Language);
        Highlighter = new SyntaxHighlighter(Document, Language);
        Document.ModifiedChanged += (_, _) => OnPropertyChanged(nameof(IsDirty));
        Document.Changed += (_, change) =>
        {
            Folding.OnTextChanged(change);
            MapInlayHints(change);
            MapSymbolScopes(change);
            if (_diagnostics.Count > 0)
            {
                _diagnostics = _diagnostics.Select(marker => marker.Map(change)).ToList();
            }
        };

        // Up/Down step over folded blocks; a caret that lands inside one by other means (a search, Go to
        // Definition, an undo) opens it.
        Editor.LineStep = (line, direction) => Folding.Step(line, direction);
        Editor.SelectionChanged += (_, _) =>
        {
            if (Folding.HasFolds)
            {
                foreach (var selection in Editor.Selections.All)
                {
                    Folding.Reveal(Document.GetLineOfOffset(selection.Active));
                }
            }
        };
    }

    /// <summary>This document's folded regions (kept while the tab is open).</summary>
    public FoldingState Folding { get; } = new();

    private IReadOnlyList<ScopeRange> _foldRanges = [];
    private int _foldRangesVersion = -1;

    /// <summary>The blocks this document can fold (by brackets, or by indentation), computed again after edits.</summary>
    public IReadOnlyList<ScopeRange> FoldRanges()
    {
        if (_foldRangesVersion != Document.Version)
        {
            _foldRanges = StickyScopes.FoldRanges(Document, Language, Editor.Options.TabSize);
            _foldRangesVersion = Document.Version;
        }

        return _foldRanges;
    }

    /// <summary>Folds the innermost open block around the caret; a caret inside it moves to its first line.</summary>
    public void FoldAtCaret()
    {
        var line = Editor.CaretPosition.Line;
        if (FoldRanges().Where(r => r.StartLine <= line && line <= r.EndLine && !Folding.IsCollapsed(r.StartLine))
                .OrderBy(r => r.EndLine - r.StartLine).Cast<ScopeRange?>().FirstOrDefault() is { } range)
        {
            Fold(range);
        }
    }

    /// <summary>Unfolds the folded block the caret is on (its first line), or the innermost folded one around it.</summary>
    public void UnfoldAtCaret()
    {
        var line = Editor.CaretPosition.Line;
        if (Folding.Collapsed.Where(r => r.StartLine <= line && line <= r.EndLine).OrderBy(r => r.EndLine - r.StartLine).Cast<ScopeRange?>().FirstOrDefault() is { } range)
        {
            Folding.Expand(range.StartLine);
        }
    }

    /// <summary>Folds every block (the caret moves to the first line of the outermost block it's in).</summary>
    public void FoldAll()
    {
        var ranges = FoldRanges();
        var line = Editor.CaretPosition.Line;
        if (ranges.Where(r => r.StartLine < line && line <= r.EndLine).OrderBy(r => r.StartLine).Cast<ScopeRange?>().FirstOrDefault() is { } outermost)
        {
            Editor.SetCaret(Document.GetLineEnd(outermost.StartLine));
        }

        Folding.CollapseAll(ranges);
    }

    public void UnfoldAll() => Folding.ExpandAll();

    /// <summary>Folds or unfolds a block (a chevron click).</summary>
    public void ToggleFold(ScopeRange range)
    {
        if (Folding.IsCollapsed(range.StartLine))
        {
            Folding.Expand(range.StartLine);
        }
        else
        {
            Fold(range);
        }
    }

    private void Fold(ScopeRange range)
    {
        var line = Editor.CaretPosition.Line;
        if (line > range.StartLine && line <= range.EndLine)
        {
            Editor.SetCaret(Document.GetLineEnd(range.StartLine)); // first, or the caret would open it again
        }

        Folding.Collapse(range);
    }

    /// <summary>Raised when a language server sends new diagnostics for this document.</summary>
    public event EventHandler? DiagnosticsChanged;

    /// <summary>Raised when the editor's indentation options were replaced (settings, detection).</summary>
    public event EventHandler? OptionsChanged;

    public void NotifyOptionsChanged() => OptionsChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Problems in this document, kept in place while the user edits.</summary>
    public IReadOnlyList<DiagnosticMarker> Diagnostics => _diagnostics;

    public TextDocument Document { get; }

    public EditorState Editor { get; }

    public SyntaxHighlighter Highlighter { get; }

    public LanguageDefinition Language { get; }

    public Encoding Encoding { get; }

    public override string? FilePath
    {
        get => _filePath;
        set
        {
            if (SetProperty(ref _filePath, value))
            {
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Description));
            }
        }
    }

    public override string Title => FilePath is null ? UntitledName : Path.GetFileName(FilePath);

    /// <summary>Folder path shown next to the title and in tooltips.</summary>
    public override string Description => FilePath is null ? "Unsaved file" : FilePath;

    public override bool IsDirty => Document.IsModified;

    public string EncodingName => Encoding switch
    {
        UTF8Encoding utf8 when utf8.GetPreamble().Length > 0 => "UTF-8 with BOM",
        UTF8Encoding => "UTF-8",
        UnicodeEncoding when Encoding.CodePage == Encoding.BigEndianUnicode.CodePage => "UTF-16 BE",
        UnicodeEncoding => "UTF-16 LE",
        _ => Encoding.WebName.ToUpperInvariant(),
    };

    private string UntitledName { get; }

    public void SetDiagnostics(IReadOnlyList<Diagnostic> diagnostics)
    {
        _diagnostics = diagnostics.Select(d => DiagnosticMarker.From(d, Document)).OrderBy(m => m.Start).ToList();
        DiagnosticsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised when the lines changed since the staged version were recomputed.</summary>
    public event EventHandler? LineChangesChanged;

    /// <summary>Lines changed compared with the file's staged (git index) version; the gutter shows them.</summary>
    public IReadOnlyList<DiffHunk> LineChanges => _lineChanges;

    /// <summary>The staged version's lines that <see cref="LineChanges"/> compare against (to show or restore them).</summary>
    public IReadOnlyList<string> ChangeBase { get; private set; } = [];

    public void SetLineChanges(IReadOnlyList<DiffHunk> changes, IReadOnlyList<string>? changeBase = null)
    {
        ChangeBase = changeBase ?? [];
        if (changes.Count == 0 && _lineChanges.Count == 0)
        {
            return;
        }

        _lineChanges = changes;
        LineChangesChanged?.Invoke(this, EventArgs.Empty);
    }

    private IReadOnlyDictionary<int, IReadOnlyList<InlayHint>> _inlayHints = new Dictionary<int, IReadOnlyList<InlayHint>>();

    /// <summary>Raised when the inlay hints were replaced (not when edits moved them).</summary>
    public event EventHandler? InlayHintsChanged;

    /// <summary>The inlay hints of a line, in column order; empty when there are none.</summary>
    public IReadOnlyList<InlayHint> InlayHintsOn(int line) => _inlayHints.TryGetValue(line, out var hints) ? hints : [];

    /// <summary>Replaces the inlay hints (fresh from the server for the current text).</summary>
    public void SetInlayHints(IReadOnlyList<InlayHint> hints)
    {
        _inlayHints = hints.GroupBy(h => h.Position.Line).ToDictionary(g => g.Key, g => (IReadOnlyList<InlayHint>)g.OrderBy(h => h.Position.Column).ToList());
        InlayHintsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Keeps hints in place while typing, until fresh ones arrive: lines below an edit move with it, and
    /// the edited lines lose theirs (their columns no longer fit).
    /// </summary>
    private void MapInlayHints(TextChange change)
    {
        if (_inlayHints.Count == 0)
        {
            return;
        }

        var removedLines = change.RemovedLineBreaks;
        var delta = change.InsertedLineBreaks - removedLines;
        var lastEdited = change.StartLine + removedLines;
        var mapped = new Dictionary<int, IReadOnlyList<InlayHint>>();
        foreach (var (line, hints) in _inlayHints)
        {
            if (line < change.StartLine)
            {
                mapped[line] = hints;
            }
            else if (line > lastEdited)
            {
                mapped[line + delta] = delta == 0 ? hints : hints.Select(h => h with { Position = h.Position with { Line = line + delta } }).ToList();
            }
        }

        _inlayHints = mapped;
    }

    /// <summary>Raised when the language server's scopes (classes, methods…) were replaced.</summary>
    public event EventHandler? SymbolScopesChanged;

    /// <summary>
    /// Blocks from the language server's document symbols, for sticky scroll; empty when there is no
    /// server (the editor then finds blocks from brackets or indentation alone).
    /// </summary>
    public IReadOnlyList<ScopeRange> SymbolScopes { get; private set; } = [];

    public void SetSymbolScopes(IReadOnlyList<ScopeRange> scopes)
    {
        SymbolScopes = scopes;
        SymbolScopesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Moves the server's scopes with lines added or removed above them, until fresh ones arrive.</summary>
    private void MapSymbolScopes(TextChange change)
    {
        var delta = change.InsertedLineBreaks - change.RemovedLineBreaks;
        if (SymbolScopes.Count == 0 || delta == 0)
        {
            return;
        }

        var lastEdited = change.StartLine + change.RemovedLineBreaks;
        SymbolScopes = SymbolScopes
            .Select(s => new ScopeRange(s.StartLine > lastEdited ? s.StartLine + delta : s.StartLine, s.EndLine >= lastEdited ? s.EndLine + delta : s.EndLine))
            .Where(s => s.EndLine > s.StartLine)
            .ToList();
    }

    private IReadOnlyList<MergeConflict> _conflicts = [];
    private int _conflictsVersion = -1;

    /// <summary>The merge conflict blocks git left in this document (found again only after the text changed).</summary>
    public IReadOnlyList<MergeConflict> Conflicts
    {
        get
        {
            if (_conflictsVersion != Document.Version)
            {
                _conflicts = MergeConflicts.Find(Document);
                _conflictsVersion = Document.Version;
            }

            return _conflicts;
        }
    }

    /// <summary>
    /// Shows the file's new content from disk (changed by git, a formatter or another program). A tab with
    /// unsaved changes keeps them: the user's work is never thrown away. The reload is one undo step, so
    /// it can be taken back, and the tab stays clean afterwards. Returns whether the text changed.
    /// </summary>
    public bool ReloadFromDisk(string text)
    {
        if (IsDirty)
        {
            return false;
        }

        var version = Document.Version;
        Document.LineEnding = LineEndings.Detect(text);
        Editor.ReplaceText(LineEndings.Normalize(text));
        Document.MarkSaved();
        return Document.Version != version;
    }

    public override void Close() => Highlighter.Detach();
}
