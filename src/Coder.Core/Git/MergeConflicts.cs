using Coder.Core.Editing;
using Coder.Core.Text;

namespace Coder.Core.Git;

/// <summary>Which side of a merge conflict to keep.</summary>
public enum ConflictResolution
{
    /// <summary>The lines between <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> and <c>=======</c>: your branch (HEAD).</summary>
    Current,

    /// <summary>The lines between <c>=======</c> and <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt;</c>: the branch being merged in.</summary>
    Incoming,

    /// <summary>Current, then incoming.</summary>
    Both,
}

/// <summary>
/// One conflict block left by git in a file, as line numbers of its markers:
/// <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt; ours</c>, optionally <c>||||||| base</c> (diff3 style), <c>=======</c> and <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt; theirs</c>.
/// </summary>
/// <param name="Base">The <c>|||||||</c> line, or -1 when the block has no common-ancestor section.</param>
public readonly record struct MergeConflict(int Start, int Base, int Separator, int End)
{
    /// <summary>First and last line of the current (ours) lines; empty when Last &lt; First.</summary>
    public (int First, int Last) Current => (Start + 1, (Base >= 0 ? Base : Separator) - 1);

    /// <summary>The common ancestor's lines (diff3 style only).</summary>
    public (int First, int Last) Ancestor => Base >= 0 ? (Base + 1, Separator - 1) : (Separator, Separator - 1);

    public (int First, int Last) Incoming => (Separator + 1, End - 1);
}

/// <summary>Finds git's conflict markers in a document and resolves them.</summary>
public static class MergeConflicts
{
    private const int MarkerLength = 7;

    /// <summary>
    /// The complete conflict blocks in the document, top to bottom. Incomplete or nested markers are ignored,
    /// so ordinary text that happens to start with "=======" (a Markdown heading underline) is left alone.
    /// </summary>
    public static IReadOnlyList<MergeConflict> Find(TextDocument document)
    {
        List<MergeConflict>? conflicts = null;
        int start = -1, ancestor = -1, separator = -1;
        for (var line = 0; line < document.LineCount; line++)
        {
            // Checking the first character first keeps this cheap: most lines are skipped without copying them.
            if (document.GetLineLength(line) < MarkerLength || document[document.GetLineStart(line)] is not ('<' or '|' or '=' or '>'))
            {
                continue;
            }

            switch (MarkerOf(document.GetLineText(line)))
            {
                case '<':
                    (start, ancestor, separator) = (line, -1, -1);
                    break;
                case '|' when start >= 0 && ancestor < 0 && separator < 0:
                    ancestor = line;
                    break;
                case '=' when start >= 0 && separator < 0:
                    separator = line;
                    break;
                case '>' when start >= 0 && separator >= 0:
                    (conflicts ??= []).Add(new MergeConflict(start, ancestor, separator, line));
                    start = -1;
                    break;
            }
        }

        return conflicts ?? (IReadOnlyList<MergeConflict>)[];
    }

    /// <summary>Replaces one conflict block with the chosen side, as one undo step.</summary>
    public static void Resolve(EditorState editor, MergeConflict conflict, ConflictResolution resolution) =>
        editor.ApplyEdits([Edit(editor.Document, conflict, resolution)]);

    /// <summary>Resolves every conflict in the document the same way, as one undo step. Returns how many there were.</summary>
    public static int ResolveAll(EditorState editor, ConflictResolution resolution)
    {
        var conflicts = Find(editor.Document);
        editor.ApplyEdits(conflicts.Select(conflict => Edit(editor.Document, conflict, resolution)).ToList());
        return conflicts.Count;
    }

    /// <summary>The edit that replaces the block's lines (markers included) with the kept lines.</summary>
    public static (int Start, int End, string Text) Edit(TextDocument document, MergeConflict conflict, ConflictResolution resolution)
    {
        var kept = resolution switch
        {
            ConflictResolution.Current => Lines(document, conflict.Current),
            ConflictResolution.Incoming => Lines(document, conflict.Incoming),
            _ => Lines(document, conflict.Current).Concat(Lines(document, conflict.Incoming)),
        };

        var text = string.Concat(kept.Select(line => line + "\n"));
        var start = document.GetLineStart(conflict.Start);
        if (conflict.End < document.LineCount - 1)
        {
            return (start, document.GetLineStart(conflict.End + 1), text); // whole lines, with their line breaks
        }

        // The block ends the file: keep no trailing line break that the file didn't have.
        return (start, document.Length, text.Length > 0 ? text[..^1] : text);
    }

    private static IEnumerable<string> Lines(TextDocument document, (int First, int Last) range)
    {
        for (var line = range.First; line <= range.Last; line++)
        {
            yield return document.GetLineText(line);
        }
    }

    /// <summary>The marker character when <paramref name="text"/> is a conflict marker line, else '\0'.</summary>
    private static char MarkerOf(string text)
    {
        var marker = text[0];
        for (var i = 1; i < MarkerLength; i++)
        {
            if (text[i] != marker)
            {
                return '\0';
            }
        }

        // "<<<<<<< HEAD", "|||||||", ">>>>>>> branch": the run is exactly seven, then the end or a space.
        // "=======" stands alone on its line.
        var rest = text.AsSpan(MarkerLength);
        return marker switch
        {
            '=' when rest.Trim().IsEmpty => '=',
            '<' or '|' or '>' when rest.IsEmpty || rest[0] == ' ' => marker,
            _ => '\0',
        };
    }
}
