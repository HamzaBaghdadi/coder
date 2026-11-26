using Coder.Core.Text;

namespace Coder.Core.Editing;

/// <summary>Edits that come from outside the keyboard: formatting, renames, quick fixes.</summary>
public sealed partial class EditorState
{
    /// <summary>
    /// Makes the document read <paramref name="text"/> (line endings already normalized) in one undo step.
    /// Only the differing middle is replaced, so the caret, the scroll position and the rest of the file stay put.
    /// </summary>
    public void ReplaceText(string text)
    {
        var current = Document.GetText();
        var prefix = 0;
        while (prefix < current.Length && prefix < text.Length && current[prefix] == text[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < current.Length - prefix && suffix < text.Length - prefix && current[^(suffix + 1)] == text[^(suffix + 1)])
        {
            suffix++;
        }

        if (prefix == current.Length && prefix == text.Length)
        {
            return; // nothing differs
        }

        ApplyEdits([(prefix, current.Length - suffix, text[prefix..^suffix])]);
    }

    /// <summary>
    /// Applies non-overlapping replacements (offsets in the current text) as one undo step. Cursors move
    /// with the text around them. Several insertions at the same offset keep their given order.
    /// </summary>
    public void ApplyEdits(IReadOnlyList<(int Start, int End, string Text)> edits)
    {
        if (edits.Count == 0)
        {
            return;
        }

        // Back to front, so earlier offsets stay valid; at equal offsets the later edit goes first,
        // which leaves the earlier one in front of it.
        var ordered = edits.Select((edit, index) => (edit, index))
            .OrderByDescending(e => e.edit.Start)
            .ThenByDescending(e => e.index)
            .Select(e => e.edit)
            .ToList();

        var after = Document.Edit(EditKind.Command, _selections, edit =>
        {
            var selections = _selections.All.ToList();
            var primary = _selections.PrimaryIndex;
            void Follow(object? sender, TextChange change)
            {
                for (var i = 0; i < selections.Count; i++)
                {
                    selections[i] = Map(selections[i], change);
                }
            }

            Document.Changed += Follow;
            try
            {
                foreach (var (start, end, text) in ordered)
                {
                    edit.Replace(start, end - start, text);
                }
            }
            finally
            {
                Document.Changed -= Follow;
            }

            return SelectionSet.Create(selections, selections[primary]).Clamp(Document.Length);
        });

        AdoptEditResult(after);
    }
}
