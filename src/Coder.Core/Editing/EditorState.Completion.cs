using Coder.Core.Text;

namespace Coder.Core.Editing;

/// <summary>Accepting a completion: the chosen text replaces the typed prefix at every cursor.</summary>
public sealed partial class EditorState
{
    /// <summary>
    /// Replaces <c>[replaceStart, caret)</c> with <paramref name="text"/> at the primary cursor, and the
    /// same prefix at every other cursor that has it, then selects
    /// <paramref name="selectionStart"/>..+<paramref name="selectionLength"/> inside each inserted text
    /// (a snippet's first placeholder). <paramref name="additionalEdits"/> (such as an added import) are
    /// applied once. All of it is one undo step.
    /// </summary>
    public void InsertCompletion(int replaceStart, string text, int selectionStart, int selectionLength,
        IReadOnlyList<(int Start, int End, string Text)> additionalEdits, IReadOnlyList<(int Number, int Start, int Length)>? tabStops = null)
    {
        var primary = _selections.Primary;
        replaceStart = Math.Clamp(replaceStart, 0, primary.Start);
        var prefixLength = primary.Start - replaceStart;
        var prefix = Document.GetText(replaceStart, prefixLength);

        // Cursor edits: each cursor's selection plus the prefix before it, when that prefix matches.
        var cursorEdits = new List<CompletionEdit>();
        foreach (var selection in _selections.All)
        {
            var start = selection.Start - prefixLength;
            var hasPrefix = start >= 0 && Document.GetText(start, prefixLength) == prefix;
            var edit = new CompletionEdit(hasPrefix ? start : selection.Start, selection.End, text, IsCursor: true, IsPrimary: selection == primary);
            if (cursorEdits.Count == 0 || edit.Start >= cursorEdits[^1].End)
            {
                cursorEdits.Add(edit);
            }
        }

        var edits = cursorEdits
            .Concat(additionalEdits
                .Where(a => cursorEdits.All(c => a.End <= c.Start || a.Start >= c.End))
                .Select(a => new CompletionEdit(a.Start, a.End, a.Text, IsCursor: false, IsPrimary: false)))
            .OrderByDescending(e => e.Start)
            .ThenBy(e => e.IsCursor ? 0 : 1) // at the same offset, an added import goes before the cursor's text
            .ToList();

        var stops = new List<(int Number, int Start, int End)>();
        int snippetStart = int.MaxValue, snippetEnd = 0;
        var after = Document.Edit(EditKind.Command, _selections, edit =>
        {
            foreach (var e in edits)
            {
                edit.Replace(e.Start, e.End - e.Start, e.Text);
            }

            // Where each cursor's text landed: its start, shifted by every edit before it.
            var results = new List<Selection>();
            Selection? primaryResult = null;
            foreach (var e in cursorEdits)
            {
                var shift = edits.Where(other => other.Start < e.Start || (other.Start == e.Start && !other.IsCursor))
                    .Sum(other => other.Text.Length - (other.End - other.Start));
                var textStart = e.Start + shift;
                var anchor = textStart + selectionStart;
                snippetStart = Math.Min(snippetStart, textStart);
                snippetEnd = Math.Max(snippetEnd, textStart + text.Length);
                foreach (var stop in tabStops ?? [])
                {
                    stops.Add((stop.Number, textStart + stop.Start, textStart + stop.Start + stop.Length));
                }

                var result = new Selection(anchor, anchor + selectionLength);
                results.Add(result);
                if (e.IsPrimary)
                {
                    primaryResult = result;
                }
            }

            return SelectionSet.Create(results, primaryResult ?? results[^1]).Clamp(Document.Length);
        });

        AdoptEditResult(after);
        if (stops.Count > 0)
        {
            StartSnippet(stops, snippetStart, snippetEnd); // Tab now moves between the placeholders
        }
    }

    private readonly record struct CompletionEdit(int Start, int End, string Text, bool IsCursor, bool IsPrimary);
}
