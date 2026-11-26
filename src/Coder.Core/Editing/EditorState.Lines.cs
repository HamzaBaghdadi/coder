using Coder.Core.Text;

namespace Coder.Core.Editing;

/// <summary>
/// Whole-line operations: indent, comment, move, duplicate, delete. With several cursors they act on
/// every line any cursor touches, and never twice on the same line.
/// </summary>
public sealed partial class EditorState
{
    /// <summary>Tab: indents selected lines, or inserts indentation up to the next tab stop at each cursor.</summary>
    public void Indent()
    {
        var blockIndent = _selections.All.Any(selection =>
        {
            var (first, last) = LinesOf(selection);
            return !selection.IsEmpty && (first != last || CoversWholeLine(selection, first));
        });

        if (!blockIndent)
        {
            RunEdit(EditKind.Typing, InsertIndentAtCaret);
            return;
        }

        var unit = Options.IndentUnit;
        TransformLines(line => line.Length == 0 ? line : unit + line);
    }

    /// <summary>Shift+Tab: removes one indent level from every selected line.</summary>
    public void Outdent() => TransformLines(line => line[RemovableIndent(line)..]);

    /// <summary>Ctrl+/: comments or uncomments the selected lines with the language's line comment.</summary>
    public void ToggleLineComment()
    {
        if (Language.LineComment is not { } marker)
        {
            return;
        }

        var nonBlank = TouchedLines().Select(Document.GetLineText).Where(line => !string.IsNullOrWhiteSpace(line)).ToList();
        if (nonBlank.Count == 0)
        {
            return;
        }

        var allCommented = nonBlank.All(line => line.TrimStart().StartsWith(marker, StringComparison.Ordinal));
        var column = nonBlank.Min(line => TextColumns.LeadingWhitespaceLength(line));
        TransformLines(line =>
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return line;
            }

            if (!allCommented)
            {
                return line[..column] + marker + " " + line[column..];
            }

            var at = line.IndexOf(marker, StringComparison.Ordinal);
            var length = marker.Length + (at + marker.Length < line.Length && line[at + marker.Length] == ' ' ? 1 : 0);
            return line.Remove(at, length);
        });
    }

    /// <summary>Alt+Up / Alt+Down: swaps the selected lines with the line above or below.</summary>
    public void MoveLines(bool up)
    {
        var groups = LineGroups();
        if (up ? groups[0].FirstLine == 0 : groups[^1].LastLine == Document.LineCount - 1)
        {
            return;
        }

        EditGroups(EditKind.Command, groups, (edit, group) =>
        {
            var block = LinesText(group.FirstLine, group.LastLine);
            var neighbourLine = up ? group.FirstLine - 1 : group.LastLine + 1;
            var neighbour = Document.GetLineText(neighbourLine);
            var regionStart = Document.GetLineStart(up ? neighbourLine : group.FirstLine);
            var regionEnd = Document.GetLineEnd(up ? group.LastLine : neighbourLine);
            var shift = (neighbour.Length + 1) * (up ? -1 : 1);

            edit.Replace(regionStart, regionEnd - regionStart, up ? block + "\n" + neighbour : neighbour + "\n" + block);
            return group.Selections.Select(s => new Selection(s.Anchor + shift, s.Active + shift)).ToList();
        });
    }

    /// <summary>Shift+Alt+Up / Down: duplicates the selected lines; the cursors follow the copy when going down.</summary>
    public void DuplicateLines(bool down) =>
        EditGroups(EditKind.Command, LineGroups(), (edit, group) =>
        {
            var block = LinesText(group.FirstLine, group.LastLine);
            edit.Insert(Document.GetLineEnd(group.LastLine), "\n" + block);
            var shift = down ? block.Length + 1 : 0;
            return group.Selections.Select(s => new Selection(s.Anchor + shift, s.Active + shift)).ToList();
        });

    /// <summary>Ctrl+Shift+K: deletes the selected lines including their line breaks.</summary>
    public void DeleteLines() =>
        EditGroups(EditKind.Command, LineGroups(), (edit, group) =>
        {
            var start = Document.GetLineStart(group.FirstLine);
            var end = group.LastLine + 1 < Document.LineCount ? Document.GetLineStart(group.LastLine + 1) : Document.Length;
            if (end == Document.Length && group.FirstLine > 0)
            {
                start--; // Last line: remove the line break before it instead.
            }

            var columns = group.Selections.Select(s => Document.GetPosition(s.Active).Column).ToList();
            edit.Delete(start, end - start);
            var line = Math.Min(group.FirstLine, Document.LineCount - 1);
            return columns.Select(column => Selection.Caret(Document.GetOffset(new TextPosition(line, column)))).ToList();
        });

    /// <summary>Ctrl+Enter / Ctrl+Shift+Enter: opens a new indented line below or above, without splitting.</summary>
    public void InsertLineAndMove(bool above) => RunEdit(EditKind.Command, () =>
    {
        var line = CaretPosition.Line;
        var text = Document.GetLineText(line);
        var indent = text[..TextColumns.LeadingWhitespaceLength(text)];
        var at = above ? Document.GetLineStart(line) : Document.GetLineEnd(line);

        Edit(edit =>
        {
            edit.Insert(at, above ? indent + "\n" : "\n" + indent);
            return Selection.Caret(above ? at + indent.Length : at + 1 + indent.Length);
        });
    });

    private void InsertIndentAtCaret()
    {
        if (!Options.InsertSpaces)
        {
            ReplaceSelection("\t");
            return;
        }

        var caret = Document.GetPosition(Selection.Start);
        var visual = TextColumns.ToVisual(Document.GetLineText(caret.Line), caret.Column, Options.TabSize);
        ReplaceSelection(new string(' ', Options.TabSize - (visual % Options.TabSize)));
    }

    private int RemovableIndent(string line)
    {
        if (line.StartsWith('\t'))
        {
            return 1;
        }

        var spaces = 0;
        while (spaces < Options.TabSize && spaces < line.Length && line[spaces] == ' ')
        {
            spaces++;
        }

        return spaces;
    }

    private bool CoversWholeLine(Selection selection, int line) =>
        selection.Start <= Document.GetLineStart(line) && selection.End >= Document.GetLineEnd(line)
        && Document.GetLineLength(line) > 0;

    private string LinesText(int first, int last)
    {
        var start = Document.GetLineStart(first);
        return Document.GetText(start, Document.GetLineEnd(last) - start);
    }

    /// <summary>Every line touched by any selection, in order, each once.</summary>
    private List<int> TouchedLines() =>
        _selections.All
            .SelectMany(selection =>
            {
                var (first, last) = LinesOf(selection);
                return Enumerable.Range(first, last - first + 1);
            })
            .Distinct()
            .Order()
            .ToList();

    /// <summary>Cursors grouped into runs of touching lines, so a line operation handles each run once.</summary>
    private List<CaretGroup> LineGroups()
    {
        var groups = new List<CaretGroup>();
        foreach (var selection in _selections.All)
        {
            var (first, last) = LinesOf(selection);
            if (groups.Count > 0 && first <= groups[^1].LastLine + 1)
            {
                var current = groups[^1];
                groups[^1] = new CaretGroup([.. current.Selections, selection])
                {
                    FirstLine = current.FirstLine,
                    LastLine = Math.Max(current.LastLine, last),
                };
            }
            else
            {
                groups.Add(new CaretGroup([selection]) { FirstLine = first, LastLine = last });
            }
        }

        return groups;
    }

    /// <summary>
    /// Rewrites every touched line as one undo step and keeps each selection anchored to the same text,
    /// shifting each end by the length changes before it on its own line and earlier lines.
    /// </summary>
    private void TransformLines(Func<string, string> transform)
    {
        var lines = TouchedLines();
        var before = _selections;
        var endpoints = before.All.Select(s => (Anchor: Document.GetPosition(s.Anchor), Active: Document.GetPosition(s.Active))).ToList();
        var primaryIndex = before.PrimaryIndex;

        var after = Document.Edit(EditKind.Command, before, edit =>
        {
            var deltas = new Dictionary<int, int>();
            foreach (var line in lines)
            {
                var original = Document.GetLineText(line);
                var changed = transform(original);
                if (original == changed)
                {
                    continue;
                }

                edit.Replace(Document.GetLineStart(line), original.Length, changed);
                deltas[line] = changed.Length - original.Length;
            }

            int MapPosition(TextPosition position, int offset)
            {
                var shift = deltas.Where(d => d.Key < position.Line || (d.Key == position.Line && position.Column > 0)).Sum(d => d.Value);
                return Math.Max(Document.GetLineStart(position.Line), offset + shift);
            }

            var mapped = before.All
                .Select((s, i) => new Selection(MapPosition(endpoints[i].Anchor, s.Anchor), MapPosition(endpoints[i].Active, s.Active)))
                .ToList();
            return SelectionSet.Create(mapped, mapped[primaryIndex]);
        });

        AdoptEditResult(after);
    }
}
