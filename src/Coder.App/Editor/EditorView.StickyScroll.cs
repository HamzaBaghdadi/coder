using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Coder.App.Theme;
using Coder.Core.Editing;
using Coder.Core.Text;

namespace Coder.App.Editor;

/// <summary>
/// Sticky scroll, like VS Code's: the header lines of the blocks enclosing the top of the viewport
/// (namespace, class, method, if…) stay pinned there, stacked by nesting, with their colors and line
/// numbers. When a block ends, its header is pushed up by the next line. Clicking one goes to it.
/// <para>The blocks (<see cref="ScopeIndex"/>) are found once per text version, a moment after typing
/// stops; each frame only looks up the few around the top line.</para>
/// </summary>
public sealed partial class EditorView
{
    private const int StickyMaxLines = 5;
    private const double StickyShadowHeight = 6;
    private const int LargeDocumentLines = 5_000;

    private ScopeIndex _scopes = ScopeIndex.Empty;
    private int _scopesVersion = -1;
    private DispatcherTimer? _scopesTimer;
    private Func<IReadOnlyList<ScopeRange>> _symbolScopes = () => [];
    private bool _stickyScroll = true;

    // What the last frame pinned (for clicks and for keeping their layouts cached), and the hovered row.
    private readonly List<(ScopeRange Scope, double Top)> _stickyRows = [];
    private readonly HashSet<int> _stickyLines = [];
    private int _stickyHover = -1;

    /// <summary>Whether block headers stay pinned at the top while scrolling.</summary>
    public bool StickyScroll
    {
        get => _stickyScroll;
        set
        {
            if (_stickyScroll != value)
            {
                _stickyScroll = value;
                _scopesVersion = -1;
                InvalidateVisual();
            }
        }
    }

    /// <summary>The lines the sticky header currently pins, top first (tests).</summary>
    internal IReadOnlyList<int> StickyLines => _stickyRows.Select(row => row.Scope.StartLine).ToList();

    /// <summary>Where the language server's blocks (classes, methods) come from.</summary>
    public void SetSymbolScopesSource(Func<IReadOnlyList<ScopeRange>> scopes)
    {
        _symbolScopes = scopes;
        SymbolScopesChanged();
    }

    /// <summary>The server's blocks were replaced: find the scopes again now.</summary>
    public void SymbolScopesChanged()
    {
        _scopesVersion = -1;
        InvalidateVisual();
    }

    /// <summary>After an edit, the scopes are found again once typing pauses (the old ones serve meanwhile).</summary>
    private void ScheduleScopes()
    {
        if (!_stickyScroll)
        {
            return;
        }

        if (_editor is { } editor && editor.Document.LineCount < LargeDocumentLines)
        {
            return; // small enough to redo on the next frame
        }

        _scopesTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            _scopesTimer!.Stop();
            _scopesVersion = -1;
            InvalidateVisual();
        });
        _scopesTimer.Stop();
        _scopesTimer.Start();
    }

    private ScopeIndex Scopes()
    {
        var document = _editor!.Document;
        var waiting = _scopesTimer?.IsEnabled == true && _scopesVersion >= 0;
        if (_scopesVersion != document.Version && !waiting)
        {
            _scopes = StickyScopes.Compute(document, _editor.Language, TabSize, _symbolScopes());
            _scopesVersion = document.Version;
        }

        return _scopes;
    }

    /// <summary>Height the pinned headers cover (for keeping the caret out from under them).</summary>
    private double StickyHeight => _stickyRows.Count == 0 ? 0 : _stickyRows.Max(row => row.Top) + _metrics.LineHeight;

    private void DrawStickyScroll(DrawingContext context, Rect bounds)
    {
        _stickyRows.Clear();
        _stickyLines.Clear();
        if (!_stickyScroll || _editor is null)
        {
            return;
        }

        var lineHeight = _metrics.LineHeight;
        var topLine = (int)(_offset.Y / lineHeight);
        var maxLines = Math.Min(StickyMaxLines, (int)(bounds.Height / lineHeight / 3));
        if (maxLines <= 0)
        {
            return;
        }

        var headers = Scopes().StickyHeaders(topLine, maxLines);
        for (var i = 0; i < headers.Count; i++)
        {
            // A block's header never goes below its last line: the next line pushes it up.
            var pushedTop = LineTop(headers[i].EndLine + 1) - lineHeight;
            _stickyRows.Add((headers[i], Math.Min(i * lineHeight, pushedTop)));
            _stickyLines.Add(headers[i].StartLine);
        }

        if (_stickyRows.Count == 0)
        {
            return;
        }

        var width = bounds.Width - MinimapReserve;
        var gutterWidth = GutterWidth;
        var bottom = StickyHeight;
        if (bottom <= 0)
        {
            _stickyRows.Clear();
            return;
        }

        using (context.PushClip(new Rect(0, 0, width, bottom)))
        {
            // Inner rows first, so a pushed-up row slides under the one above it.
            for (var i = _stickyRows.Count - 1; i >= 0; i--)
            {
                var (scope, top) = _stickyRows[i];
                var row = new Rect(0, top, width, lineHeight);
                context.FillRectangle(Palette.EditorBackground, row);
                if (i == _stickyHover)
                {
                    context.FillRectangle(Palette.Hover, row);
                }

                using (context.PushClip(new Rect(gutterWidth, top, Math.Max(0, width - gutterWidth), lineHeight)))
                {
                    var layout = LayoutOf(scope.StartLine);
                    layout.Layout.Draw(context, new Point(LayoutLeft(layout), top + _metrics.TextTop));
                }

                var number = _lineNumbers.Get(scope.StartLine + 1, false, _metrics);
                context.DrawText(number, new Point(gutterWidth - GutterPaddingRight - number.Width, top + _metrics.TextTop));
            }
        }

        context.FillRectangle(Palette.Border, new Rect(0, bottom - 1, width, 1));
        context.FillRectangle(StickyShadowBrush(), new Rect(0, bottom, width, StickyShadowHeight));
    }

    private LinearGradientBrush? _stickyShadow;
    private Color _stickyShadowColor;

    private LinearGradientBrush StickyShadowBrush()
    {
        var color = Palette.Shadow.Color;
        if (_stickyShadow is null || color != _stickyShadowColor)
        {
            _stickyShadowColor = color;
            _stickyShadow = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb((byte)(color.A / 2), color.R, color.G, color.B), 0),
                    new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1),
                },
            };
        }

        return _stickyShadow;
    }

    /// <summary>The pinned row under <paramref name="point"/>, or -1.</summary>
    private int StickyRowAt(Point point)
    {
        if (point.X < 0 || point.X >= Bounds.Width - MinimapReserve)
        {
            return -1;
        }

        // Later rows can sit under earlier ones (pushed up): the earlier one is on top.
        for (var i = 0; i < _stickyRows.Count; i++)
        {
            var top = _stickyRows[i].Top;
            if (point.Y >= Math.Max(0, top) && point.Y < top + _metrics.LineHeight)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>A click on a pinned header goes to it: the caret moves there and it scrolls to its row.</summary>
    private bool StickyPressed(PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        var row = StickyRowAt(point.Position);
        if (!point.Properties.IsLeftButtonPressed || row < 0)
        {
            return false;
        }

        var line = _stickyRows[row].Scope.StartLine;
        Focus();
        var document = _editor!.Document;
        var text = document.GetLineText(line);
        _editor.SetCaret(document.GetLineStart(line) + TextColumns.LeadingWhitespaceLength(text));
        // Its outer headers stay pinned above it, so it lands just below them, where it was.
        Offset = new Vector(_offset.X, (line - row) * _metrics.LineHeight);
        RaiseScrollInvalidated(EventArgs.Empty);
        e.Handled = true;
        return true;
    }

    /// <summary>Hovering a pinned header highlights it (and keeps hover cards for the text under it away).</summary>
    private bool StickyMoved(Point position)
    {
        var row = _dragUnit is null ? StickyRowAt(position) : -1;
        if (row != _stickyHover)
        {
            _stickyHover = row;
            InvalidateVisual();
        }

        if (row < 0)
        {
            return false;
        }

        Cursor = HandCursor;
        EndHover();
        return true;
    }

    private void StickyPointerExited()
    {
        if (_stickyHover >= 0)
        {
            _stickyHover = -1;
            InvalidateVisual();
        }
    }
}
