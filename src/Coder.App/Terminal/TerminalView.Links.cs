using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Coder.App.Theme;
using Coder.Core.Terminal;

namespace Coder.App.Terminal;

/// <summary>A link in the terminal that was Ctrl+Clicked: a web address, or an existing file with the line and column it names.</summary>
public sealed record TerminalLinkTarget(TerminalLinkKind Kind, string Target, int? Line, int? Column);

/// <summary>
/// Links in the output, like VS Code's: web addresses and file paths (as compilers, test runners and
/// stack traces print them) are underlined under the pointer, and Ctrl+Click opens them. A path only
/// counts when the file exists (see <see cref="ResolveFile"/>), so ordinary words are never links.
/// </summary>
public sealed partial class TerminalView
{
    private static readonly Pen LinkPen = new(Palette.LinkUnderline, 1);

    private readonly ConditionalWeakTable<TerminalLine, LinksOfLine> _links = [];
    private (int Line, TerminalLink Link)? _hoverLink;

    /// <summary>A link was Ctrl+Clicked.</summary>
    public event EventHandler<TerminalLinkTarget>? LinkActivated;

    /// <summary>
    /// Turns a path as printed (maybe relative) into an existing file's full path, or null when there is
    /// no such file. Set by the owner, which knows the folder the shell started in.
    /// </summary>
    public Func<string, string?> ResolveFile { get; set; } = path => File.Exists(path) ? Path.GetFullPath(path) : null;

    /// <summary>For tests: the link under the pointer (underlined).</summary>
    internal TerminalLink? HoverLink => _hoverLink?.Link;

    /// <summary>For tests: the middle of a cell, in this view's coordinates.</summary>
    internal Point PointOf(int line, int column) =>
        new(Padding + (column + 0.5) * _metrics.CharWidth, Padding / 2 + (line - TopLine + 0.5) * _metrics.LineHeight);

    /// <summary>The link under a point (in this view's coordinates), if any.</summary>
    internal (int Line, TerminalLink Link)? LinkAt(Point point)
    {
        if (Buffer is not { } buffer || _selecting)
        {
            return null;
        }

        var (line, column) = CellAt(point);
        if (line < 0 || line >= buffer.TotalLines)
        {
            return null;
        }

        var link = LinksOn(buffer.LineAt(line)).FirstOrDefault(l => column >= l.StartColumn && column < l.EndColumn);
        return link is null ? null : (line, link);
    }

    /// <summary>The line's links (files only when they exist), found once per version of the line.</summary>
    private IReadOnlyList<TerminalLink> LinksOn(TerminalLine line)
    {
        if (_links.TryGetValue(line, out var cached) && cached.Version == line.Version)
        {
            return cached.Links;
        }

        var links = TerminalLinks.Find(line).Where(l => l.Kind == TerminalLinkKind.Url || ResolveFile(l.Target) is not null).ToList();
        _links.AddOrUpdate(line, new LinksOfLine(line.Version, links));
        return links;
    }

    /// <summary>Hovering: underline the link and offer Ctrl+Click; the hand cursor while Ctrl is held.</summary>
    private void UpdateHoverLink(Point point, KeyModifiers modifiers)
    {
        // A command mark in the left margin explains itself.
        if (CommandMarkTip(point) is { } tip)
        {
            _hoverLink = null;
            ToolTip.SetTip(this, tip);
            InvalidateVisual();
            return;
        }

        var hover = LinkAt(point);
        Cursor = hover is not null && modifiers.HasFlag(KeyModifiers.Control) ? new Cursor(StandardCursorType.Hand) : new Cursor(StandardCursorType.Ibeam);
        if (hover?.Link == _hoverLink?.Link && hover?.Line == _hoverLink?.Line && ToolTip.GetTip(this) is null == hover is null)
        {
            return;
        }

        _hoverLink = hover;
        ToolTip.SetTip(this, hover is { } h
            ? $"{(h.Link.Kind == TerminalLinkKind.Url ? "Open in the browser" : "Open the file")} (Ctrl+Click)"
            : null);
        InvalidateVisual();
    }

    /// <summary>Ctrl+Click on a link opens it. Returns true when it did.</summary>
    private bool TryActivateLink(Point point, KeyModifiers modifiers)
    {
        if (!modifiers.HasFlag(KeyModifiers.Control) || LinkAt(point) is not { Link: var link })
        {
            return false;
        }

        var target = link.Kind == TerminalLinkKind.Url ? link.Target : ResolveFile(link.Target);
        if (target is not null)
        {
            LinkActivated?.Invoke(this, new TerminalLinkTarget(link.Kind, target, link.Line, link.Column));
        }

        return true;
    }

    private void DrawHoverLink(DrawingContext context, int top)
    {
        if (_hoverLink is not { } hover || hover.Line < top || Buffer is not { } buffer || hover.Line >= top + buffer.Rows)
        {
            return;
        }

        var y = Math.Round(Padding / 2 + (hover.Line - top + 1) * _metrics.LineHeight - 2) + 0.5;
        context.DrawLine(LinkPen, new Point(Padding + hover.Link.StartColumn * _metrics.CharWidth, y), new Point(Padding + hover.Link.EndColumn * _metrics.CharWidth, y));
    }

    private sealed record LinksOfLine(int Version, IReadOnlyList<TerminalLink> Links);
}
