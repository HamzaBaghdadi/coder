using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Coder.Core.Editing;
using Coder.Core.Search;
using Coder.Presentation.Editors;

namespace Coder.App.Editor;

/// <summary>
/// The editor area for the active tab: an <see cref="EditorView"/> inside a scroll viewer. One instance
/// serves all tabs; switching tabs swaps the model and restores that tab's scroll position.
/// </summary>
public sealed class CodeEditor : ScrollViewer
{
    public static readonly StyledProperty<EditorTabViewModel?> TabProperty =
        AvaloniaProperty.Register<CodeEditor, EditorTabViewModel?>(nameof(Tab));

    public static readonly StyledProperty<double> EditorFontSizeProperty =
        EditorView.EditorFontSizeProperty.AddOwner<CodeEditor>();

    public static readonly StyledProperty<string> EditorFontFamilyProperty =
        EditorView.EditorFontFamilyProperty.AddOwner<CodeEditor>();

    public static readonly StyledProperty<double> EditorLineHeightProperty =
        EditorView.EditorLineHeightProperty.AddOwner<CodeEditor>();

    public static readonly StyledProperty<string> CursorStyleProperty =
        EditorView.CursorStyleProperty.AddOwner<CodeEditor>();

    public static readonly StyledProperty<bool> CursorBlinkingProperty =
        EditorView.CursorBlinkingProperty.AddOwner<CodeEditor>();

    public static readonly StyledProperty<IReadOnlyList<SearchMatch>> SearchMatchesProperty =
        AvaloniaProperty.Register<CodeEditor, IReadOnlyList<SearchMatch>>(nameof(SearchMatches), []);

    public static readonly StyledProperty<bool> ShowMinimapProperty =
        AvaloniaProperty.Register<CodeEditor, bool>(nameof(ShowMinimap), true);

    public static readonly StyledProperty<bool> MinimapRenderCharactersProperty =
        AvaloniaProperty.Register<CodeEditor, bool>(nameof(MinimapRenderCharacters));

    public static readonly StyledProperty<bool> StickyScrollProperty =
        AvaloniaProperty.Register<CodeEditor, bool>(nameof(StickyScroll), true);

    public static readonly StyledProperty<int> CurrentSearchMatchProperty =
        AvaloniaProperty.Register<CodeEditor, int>(nameof(CurrentSearchMatch), -1);

    private readonly ConditionalWeakTable<EditorState, StrongBox<Vector>> _scrollPositions = [];
    private readonly EditorView _view = new();
    private EditorTabViewModel? _attachedTab;

    public CodeEditor()
    {
        Content = _view;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _view.Bind(EditorView.EditorFontSizeProperty, this.GetObservable(EditorFontSizeProperty));
        _view.Bind(EditorView.EditorFontFamilyProperty, this.GetObservable(EditorFontFamilyProperty));
        _view.Bind(EditorView.EditorLineHeightProperty, this.GetObservable(EditorLineHeightProperty));
        _view.Bind(EditorView.CursorStyleProperty, this.GetObservable(CursorStyleProperty));
        _view.Bind(EditorView.CursorBlinkingProperty, this.GetObservable(CursorBlinkingProperty));
        _view.Scrolled += (_, offset) =>
        {
            if (_view.Editor is { } editor)
            {
                _scrollPositions.GetOrCreateValue(editor).Value = offset;
            }

            Scrolled?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>The text scrolled (overlays anchored to text must move).</summary>
    public event EventHandler? Scrolled;

    /// <inheritdoc cref="EditorView.HoverRequested"/>
    public event EventHandler<int>? HoverRequested
    {
        add => _view.HoverRequested += value;
        remove => _view.HoverRequested -= value;
    }

    /// <inheritdoc cref="EditorView.HoverEnded"/>
    public event EventHandler? HoverEnded
    {
        add => _view.HoverEnded += value;
        remove => _view.HoverEnded -= value;
    }

    /// <inheritdoc cref="EditorView.ChangeMarkerClicked"/>
    public event EventHandler<Coder.Core.Diff.DiffHunk>? ChangeMarkerClicked
    {
        add => _view.ChangeMarkerClicked += value;
        remove => _view.ChangeMarkerClicked -= value;
    }

    /// <inheritdoc cref="EditorView.DefinitionRequested"/>
    public event EventHandler? DefinitionRequested
    {
        add => _view.DefinitionRequested += value;
        remove => _view.DefinitionRequested -= value;
    }

    protected override Type StyleKeyOverride => typeof(ScrollViewer);

    public EditorTabViewModel? Tab
    {
        get => GetValue(TabProperty);
        set => SetValue(TabProperty, value);
    }

    public double EditorFontSize
    {
        get => GetValue(EditorFontSizeProperty);
        set => SetValue(EditorFontSizeProperty, value);
    }

    /// <summary>Find results to highlight.</summary>
    public IReadOnlyList<SearchMatch> SearchMatches
    {
        get => GetValue(SearchMatchesProperty);
        set => SetValue(SearchMatchesProperty, value);
    }

    public int CurrentSearchMatch
    {
        get => GetValue(CurrentSearchMatchProperty);
        set => SetValue(CurrentSearchMatchProperty, value);
    }

    /// <summary>Moves keyboard focus into the text.</summary>
    public void FocusText() => _view.Focus();

    /// <summary>The box of the character at <paramref name="offset"/>, in <paramref name="relativeTo"/>'s coordinates.</summary>
    public Rect? CharacterBounds(int offset, Visual relativeTo)
    {
        if (_view.Editor is null)
        {
            return null;
        }

        var bounds = _view.CharacterBounds(offset);
        return _view.TranslatePoint(bounds.TopLeft, relativeTo) is { } topLeft ? new Rect(topLeft, bounds.Size) : null;
    }

    /// <summary>The visible text area in <paramref name="relativeTo"/>'s coordinates.</summary>
    public Rect? TextArea(Visual relativeTo) =>
        _view.TranslatePoint(default, relativeTo) is { } topLeft ? new Rect(topLeft, _view.Bounds.Size) : null;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TabProperty)
        {
            var tab = Tab;
            var offset = tab is not null && _scrollPositions.TryGetValue(tab.Editor, out var box) ? box.Value : default;
            _view.SetFolding(tab?.Folding, tab is null ? null : tab.FoldRanges, tab is null ? null : tab.ToggleFold); // first: the scroll position is in rows, which depend on the folds
            _view.Attach(tab?.Editor, tab?.Highlighter, offset);
            AttachDiagnostics(tab);
            _view.InlineSuggestionChanged();
        }
        else if (change.Property == ShowMinimapProperty)
        {
            _view.ShowMinimap = ShowMinimap;
        }
        else if (change.Property == MinimapRenderCharactersProperty)
        {
            _view.MinimapRenderCharacters = MinimapRenderCharacters;
        }
        else if (change.Property == StickyScrollProperty)
        {
            _view.StickyScroll = StickyScroll;
        }
        else if (change.Property == SearchMatchesProperty || change.Property == CurrentSearchMatchProperty)
        {
            _view.SetSearchMatches(SearchMatches, CurrentSearchMatch);
        }
    }

    private void AttachDiagnostics(EditorTabViewModel? tab)
    {
        if (_attachedTab is not null)
        {
            _attachedTab.DiagnosticsChanged -= OnDiagnosticsChanged;
            _attachedTab.LineChangesChanged -= OnDiagnosticsChanged;
            _attachedTab.InlayHintsChanged -= OnInlayHintsChanged;
            _attachedTab.SymbolScopesChanged -= OnSymbolScopesChanged;
        }

        _attachedTab = tab;
        if (tab is not null)
        {
            tab.DiagnosticsChanged += OnDiagnosticsChanged;
            tab.LineChangesChanged += OnDiagnosticsChanged;
            tab.InlayHintsChanged += OnInlayHintsChanged;
            tab.SymbolScopesChanged += OnSymbolScopesChanged;
        }

        _view.SetSymbolScopesSource(tab is null ? () => [] : () => tab.SymbolScopes);

        _view.SetDiagnosticsSource(tab is null ? () => [] : () => tab.Diagnostics);
        _view.SetLineChangesSource(tab is null ? () => [] : () => tab.LineChanges);
        _view.SetConflictsSource(tab is null ? () => [] : () => tab.Conflicts);
        _view.SetInlayHintsSource(tab is null ? _ => [] : tab.InlayHintsOn);
    }

    /// <summary>Shows who last changed the caret's line at its end; repaints when the blame changes.</summary>
    public void SetBlame(Coder.Presentation.SourceControl.BlameViewModel blame)
    {
        blame.Changed += (_, _) => _view.InvalidateVisual();
        _view.SetBlameSource(line => Tab is { } tab ? blame.Annotation(tab, line) : null);
    }


    /// <summary>Shows extensions' end-of-line decorations for this file; repaints when its decorations change.</summary>
    public void SetDecorations(Coder.Presentation.Editors.LineDecorations decorations)
    {
        decorations.Changed += (_, path) =>
        {
            if (Tab is { } tab && Coder.Core.Lsp.DocumentUri.PathComparer.Equals(tab.FilePath, path))
            {
                _view.InvalidateVisual();
            }
        };
        _view.SetDecorationsSource(line => Tab is { } tab ? decorations.For(tab.FilePath, line) : null);
    }

    /// <summary>Shows AI inline suggestions (ghost text) after the caret; repaints when they change.</summary>
    public void SetInlineSuggestions(Coder.Presentation.Agent.InlineSuggestionsViewModel suggestions)
    {
        suggestions.Changed += (_, _) => _view.InlineSuggestionChanged();

        // The debounce runs on the UI dispatcher, so nothing is posted to the UI thread from a background thread.
        suggestions.UseScheduler((delay, action) => Avalonia.Threading.DispatcherTimer.RunOnce(action, delay, Avalonia.Threading.DispatcherPriority.Background));
        _view.SetInlineSuggestionSource(() => suggestions.SuggestionFor(Tab) is { } s ? (s.Offset, s.Text) : null);
    }

    /// <summary>For tests: the inline suggestion drawn now, if any.</summary>
    internal string? ShownInlineSuggestion => _view.ShownInlineSuggestion;

    /// <summary>The code in miniature on the right edge.</summary>
    public bool ShowMinimap
    {
        get => GetValue(ShowMinimapProperty);
        set => SetValue(ShowMinimapProperty, value);
    }

    /// <summary>Words in their colors in the minimap, instead of one bar per line.</summary>
    public bool MinimapRenderCharacters
    {
        get => GetValue(MinimapRenderCharactersProperty);
        set => SetValue(MinimapRenderCharactersProperty, value);
    }

    /// <summary>Whether the shown document can go to definitions, for the Ctrl+hover underline.</summary>
    public void SetDefinitionSupport(Func<EditorTabViewModel, bool> supported) =>
        _view.CanGoToDefinition = () => Tab is { } tab && supported(tab);

    /// <summary>The view showing the text (overlays use its geometry).</summary>
    internal EditorView View => _view;

    private void OnInlayHintsChanged(object? sender, EventArgs e) => _view.InlayHintsChanged();

    private void OnSymbolScopesChanged(object? sender, EventArgs e) => _view.SymbolScopesChanged();

    /// <summary>Block headers (class, method, if…) stay pinned at the top while scrolling.</summary>
    public bool StickyScroll
    {
        get => GetValue(StickyScrollProperty);
        set => SetValue(StickyScrollProperty, value);
    }

    /// <summary>Diagnostics or gutter change markers were updated: repaint.</summary>
    private void OnDiagnosticsChanged(object? sender, EventArgs e) => _view.InvalidateVisual();
}
