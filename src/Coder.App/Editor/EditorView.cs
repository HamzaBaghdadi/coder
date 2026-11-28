using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Coder.App.Theme;
using Coder.Core.Editing;
using Coder.Core.Search;
using Coder.Core.Syntax;
using Coder.Core.Text;

namespace Coder.App.Editor;

/// <summary>
/// The code editing surface: draws the gutter and the visible lines, and turns input into
/// <see cref="EditorState"/> operations. It is hosted in a <see cref="ScrollViewer"/> and implements
/// <see cref="ILogicalScrollable"/>, so it scrolls by repainting rather than by being a huge control.
/// <para>Split across files: this one holds state and scrolling; see <c>EditorView.Rendering.cs</c>
/// and <c>EditorView.Input.cs</c>.</para>
/// </summary>
public sealed partial class EditorView : Control, ILogicalScrollable
{
    public static readonly StyledProperty<double> EditorFontSizeProperty =
        AvaloniaProperty.Register<EditorView, double>(nameof(EditorFontSize), 14);

    /// <summary>The code font family from settings; empty uses the built-in preference list (Typography.Code).</summary>
    public static readonly StyledProperty<string> EditorFontFamilyProperty =
        AvaloniaProperty.Register<EditorView, string>(nameof(EditorFontFamily), string.Empty);

    /// <summary>Line height as a multiple of the font size.</summary>
    public static readonly StyledProperty<double> EditorLineHeightProperty =
        AvaloniaProperty.Register<EditorView, double>(nameof(EditorLineHeight), Typography.LineHeightFactor);

    /// <summary>The caret shape: "line", "block" or "underline".</summary>
    public static readonly StyledProperty<string> CursorStyleProperty =
        AvaloniaProperty.Register<EditorView, string>(nameof(CursorStyle), "line");

    public static readonly StyledProperty<bool> CursorBlinkingProperty =
        AvaloniaProperty.Register<EditorView, bool>(nameof(CursorBlinking), true);

    private const double GutterPaddingLeft = 20;
    private const double GutterPaddingRight = 18;
    private const double TextPaddingLeft = 6;
    private const int WheelLines = 3;

    private readonly LineLayoutCache _layouts = new();
    private readonly DispatcherTimer _caretTimer;
    private readonly DispatcherTimer _dragScrollTimer;
    private EditorMetrics _metrics;
    private EditorState? _editor;
    private SyntaxHighlighter? _highlighter;
    private Vector _offset;
    private Size _viewport;
    private double _widestLine;
    private bool _caretVisible = true;
    private IReadOnlyList<SearchMatch> _searchMatches = [];
    private int _currentSearchMatch = -1;
    private EventHandler? _scrollInvalidated;
    private readonly EditorInputMethod _inputMethod;

    // Text an input method is composing (shown at the caret, not yet in the document).
    private string? _preedit;

    static EditorView()
    {
        FocusableProperty.OverrideDefaultValue<EditorView>(true);
    }

    public EditorView()
    {
        _metrics = CreateMetrics();
        _caretTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(530), DispatcherPriority.Render, (_, _) =>
        {
            _caretVisible = !_caretVisible;
            InvalidateVisual();
        });
        _dragScrollTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, (_, _) => DragScrollTick());

        // Input methods (Chinese, Japanese, Korean...) talk to the editor through this client.
        _inputMethod = new EditorInputMethod(this);
        TextInputMethodClientRequested += (_, e) => e.Client = _inputMethod;
    }

    /// <summary>The primary caret as a rectangle in view coordinates (the IME places its window next to it).</summary>
    internal Rect CaretRectangle
    {
        get
        {
            if (_editor is null)
            {
                return default;
            }

            var caret = _editor.CaretPosition;
            return new Rect(CaretX(caret.Line, caret.Column), LineTop(caret.Line), 2, _metrics.LineHeight);
        }
    }

    /// <summary>Shows (or clears) the text an input method is composing.</summary>
    internal void SetPreedit(string? text)
    {
        _preedit = string.IsNullOrEmpty(text) ? null : text;
        InvalidateVisual();
    }

    event EventHandler? ILogicalScrollable.ScrollInvalidated
    {
        add => _scrollInvalidated += value;
        remove => _scrollInvalidated -= value;
    }

    public double EditorFontSize
    {
        get => GetValue(EditorFontSizeProperty);
        set => SetValue(EditorFontSizeProperty, value);
    }

    public string EditorFontFamily
    {
        get => GetValue(EditorFontFamilyProperty);
        set => SetValue(EditorFontFamilyProperty, value);
    }

    public double EditorLineHeight
    {
        get => GetValue(EditorLineHeightProperty);
        set => SetValue(EditorLineHeightProperty, value);
    }

    public string CursorStyle
    {
        get => GetValue(CursorStyleProperty);
        set => SetValue(CursorStyleProperty, value);
    }

    public bool CursorBlinking
    {
        get => GetValue(CursorBlinkingProperty);
        set => SetValue(CursorBlinkingProperty, value);
    }

    public EditorState? Editor => _editor;

    /// <summary>Raised when the view scrolls, so the owner can remember the position per document.</summary>
    public event EventHandler<Vector>? Scrolled;

    // ILogicalScrollable --------------------------------------------------------------------------

    public bool CanHorizontallyScroll { get; set; }

    public bool CanVerticallyScroll { get; set; }

    public bool IsLogicalScrollEnabled => true;

    /// <summary>One wheel tick: 3 lines, like VS Code (also the scroll bar arrow step).</summary>
    public Size ScrollSize => new(_metrics.CharWidth * 4 * WheelLines, _metrics.LineHeight * WheelLines);

    public Size PageScrollSize => new(_viewport.Width, _viewport.Height - _metrics.LineHeight);

    public Size Extent
    {
        get
        {
            if (_editor is null)
            {
                return default;
            }

            // Allow scrolling the last line up to the top, like most editors.
            var height = (VisibleLineCount - 1) * _metrics.LineHeight + _viewport.Height; // folded lines take no rows
            var width = TextLeft + _widestLine + _metrics.CharWidth * 8 + MinimapReserve;
            return new Size(Math.Max(width, _viewport.Width), Math.Max(height, _viewport.Height));
        }
    }

    public Size Viewport => _viewport;

    public Vector Offset
    {
        get => _offset;
        set
        {
            var clamped = ClampOffset(value);
            if (clamped == _offset)
            {
                return;
            }

            _offset = clamped;
            InvalidateVisual();
            EndHover();
            Scrolled?.Invoke(this, _offset);
        }
    }

    public bool BringIntoView(Control target, Rect targetRect) => false;

    public Control? GetControlInDirection(NavigationDirection direction, Control? from) => null;

    public void RaiseScrollInvalidated(EventArgs e) => _scrollInvalidated?.Invoke(this, e);

    // Model ---------------------------------------------------------------------------------------

    /// <summary>Shows a different document. The view itself is reused across tabs to keep memory low.</summary>
    public void Attach(EditorState? editor, SyntaxHighlighter? highlighter, Vector scrollOffset)
    {
        if (_editor is not null)
        {
            _editor.Document.Changed -= OnDocumentChanged;
            _editor.SelectionChanged -= OnSelectionChanged;
        }

        if (_highlighter is not null)
        {
            _highlighter.Invalidated -= OnHighlightingInvalidated;
        }

        _editor = editor;
        _highlighter = highlighter;
        _layouts.Clear();
        _widestLine = 0;
        ColorsAttached();
        InvalidateMinimap();
        ClearCtrlLink();
        _scopesVersion = -1;
        _stickyHover = -1;

        if (_editor is not null)
        {
            _editor.Document.Changed += OnDocumentChanged;
            _editor.SelectionChanged += OnSelectionChanged;
        }

        if (_highlighter is not null)
        {
            _highlighter.Invalidated += OnHighlightingInvalidated;
        }

        _offset = ClampOffset(scrollOffset);
        RaiseScrollInvalidated(EventArgs.Empty);
        RestartCaretBlink();
        InvalidateVisual();
    }

    /// <summary>Find results to highlight (sorted by offset) and which one is current.</summary>
    public void SetSearchMatches(IReadOnlyList<SearchMatch> matches, int currentIndex)
    {
        _searchMatches = matches;
        _currentSearchMatch = currentIndex;
        InvalidateVisual();
    }

    // Layout --------------------------------------------------------------------------------------

    /// <summary>X where the text area starts (right edge of the gutter).</summary>
    private double TextLeft => GutterWidth + TextPaddingLeft;

    private double GutterWidth
    {
        get
        {
            var digits = Math.Max(3, (_editor?.Document.LineCount ?? 1).ToString(System.Globalization.CultureInfo.InvariantCulture).Length);
            return GutterPaddingLeft + digits * _metrics.CharWidth + GutterPaddingRight + FoldColumnWidth; // numbers, change bar, fold chevrons
        }
    }

    private int TabSize => _editor?.Options.TabSize ?? 4;

    protected override Size MeasureOverride(Size availableSize) => default;

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_viewport != finalSize)
        {
            _viewport = finalSize;
            _offset = ClampOffset(_offset);
            RaiseScrollInvalidated(EventArgs.Empty);
        }

        return finalSize;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CursorBlinkingProperty)
        {
            RestartCaretBlink();
            InvalidateVisual();
        }
        else if (change.Property == CursorStyleProperty)
        {
            InvalidateVisual();
        }

        if (change.Property == EditorFontSizeProperty || change.Property == EditorFontFamilyProperty || change.Property == EditorLineHeightProperty)
        {
            _metrics = CreateMetrics();
            _layouts.Clear();
            _widestLine = 0;
            RaiseScrollInvalidated(EventArgs.Empty);
            InvalidateVisual();
        }
    }

    /// <summary>Scrolls the minimum amount needed to show the caret, keeping a small margin.</summary>
    public void ScrollCaretIntoView()
    {
        if (_editor is null || _viewport.Height <= 0)
        {
            return;
        }

        var caret = _editor.CaretPosition;
        var x = CaretX(caret.Line, caret.Column) - TextLeft + _offset.X; // content coordinates
        var y = RowOf(caret.Line) * _metrics.LineHeight;

        var target = _offset;
        var margin = _metrics.LineHeight * 2;
        var topMargin = Math.Max(margin, StickyHeight); // not under the pinned block headers
        if (y < target.Y + topMargin)
        {
            target = target.WithY(y - topMargin);
        }
        else if (y + _metrics.LineHeight > target.Y + _viewport.Height - margin)
        {
            target = target.WithY(y + _metrics.LineHeight - _viewport.Height + margin);
        }

        var textWidth = _viewport.Width - TextLeft - MinimapReserve;
        var horizontalMargin = _metrics.CharWidth * 6;
        if (x < target.X + horizontalMargin)
        {
            target = target.WithX(x - horizontalMargin);
        }
        else if (x > target.X + textWidth - horizontalMargin)
        {
            _widestLine = Math.Max(_widestLine, x);
            target = target.WithX(x - textWidth + horizontalMargin);
        }

        if (ClampOffset(target) != _offset)
        {
            Offset = target;
            RaiseScrollInvalidated(EventArgs.Empty);
        }
    }

    private Vector ClampOffset(Vector offset)
    {
        var extent = Extent;
        var x = Math.Clamp(offset.X, 0, Math.Max(0, extent.Width - _viewport.Width));
        var y = Math.Clamp(offset.Y, 0, Math.Max(0, extent.Height - _viewport.Height));
        return new Vector(Math.Round(x), Math.Round(y));
    }

    /// <summary>The chosen family first, then the built-in list, so a missing font falls back gracefully.</summary>
    private EditorMetrics CreateMetrics()
    {
        var family = string.IsNullOrWhiteSpace(EditorFontFamily) ? Typography.Code : new FontFamily($"{EditorFontFamily}, {Typography.Code}");
        return new(new Typeface(family), EditorFontSize, EditorLineHeight);
    }

    // Change tracking ----------------------------------------------------------------------------

    private void OnDocumentChanged(object? sender, TextChange change)
    {
        InvalidateMinimap();
        ClearCtrlLink();
        _layouts.InvalidateFrom(change.StartLine);
        InvalidateColorsFrom(change.StartLine);
        ScheduleScopes();
        if (change.ChangesLineCount)
        {
            RaiseScrollInvalidated(EventArgs.Empty);
        }

        InvalidateVisual();
    }

    private void OnHighlightingInvalidated(object? sender, int line)
    {
        _layouts.InvalidateFrom(line);
        InvalidateColorsFrom(line);
        InvalidateMinimap();
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        RestartCaretBlink();
        ScrollCaretIntoView();
        _inputMethod.NotifyCaretChanged();
        InvalidateVisual();
    }

    private void RestartCaretBlink()
    {
        _caretVisible = true;
        _caretTimer.Stop();
        if (IsFocused && CursorBlinking)
        {
            _caretTimer.Start();
        }
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        RestartCaretBlink();
        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _caretTimer.Stop();
        InvalidateVisual();
    }
}
