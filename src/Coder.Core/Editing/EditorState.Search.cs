using Coder.Core.Search;
using Coder.Core.Text;

namespace Coder.Core.Editing;

/// <summary>Selecting and replacing search matches.</summary>
public sealed partial class EditorState
{
    public void Select(SearchMatch match) => SetSelection(new Selection(match.Start, match.End));

    /// <summary>Puts a cursor on every match (find widget: Alt+Enter); the current match becomes primary.</summary>
    public void SelectAll(IReadOnlyList<SearchMatch> matches, int currentIndex)
    {
        if (matches.Count == 0)
        {
            return;
        }

        var selections = matches.Select(m => new Selection(m.Start, m.End)).ToList();
        SetSelections(SelectionSet.Create(selections, selections[Math.Clamp(currentIndex, 0, selections.Count - 1)]));
    }

    /// <summary>
    /// Replaces several matches as one undo step. Replacements are computed up front (regex group
    /// references need the original text), then applied back to front so earlier offsets stay valid.
    /// </summary>
    public void ReplaceMatches(IReadOnlyList<SearchMatch> matches, Func<SearchMatch, string> replacementFor)
    {
        if (matches.Count == 0)
        {
            return;
        }

        var replacements = matches.Select(replacementFor).ToList();
        var after = Document.Edit(EditKind.Command, _selections, edit =>
        {
            for (var i = matches.Count - 1; i >= 0; i--)
            {
                edit.Replace(matches[i].Start, matches[i].Length, replacements[i]);
            }

            return SelectionSet.Single(Selection.Caret(matches[0].Start + replacements[0].Length));
        });

        AdoptEditResult(after);
    }
}
