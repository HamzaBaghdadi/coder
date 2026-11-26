using Coder.Core.Diff;
using Coder.Core.Syntax;

namespace Coder.Presentation.Editors;

public enum DiffRowKind
{
    Unchanged,
    Removed,
    Added,
    Modified,
}

/// <summary>
/// One row of a side-by-side diff: a line from the original (left), from the modified text (right),
/// or both. A null side is the empty filler that keeps the two sides aligned.
/// </summary>
public readonly record struct DiffRow(int? OldLine, int? NewLine, DiffRowKind Kind);

/// <summary>
/// A comparison of two versions of a file (e.g. the last commit and the working tree), shown as a tab.
/// It holds both texts, the changed hunks and the aligned rows the side-by-side view draws.
/// </summary>
public sealed class DiffTabViewModel : TabViewModel
{
    private string? _filePath;

    public DiffTabViewModel(string path, string originalLabel, string original, string modifiedLabel, string modified)
    {
        _filePath = path;
        OriginalLabel = originalLabel;
        ModifiedLabel = modifiedLabel;
        OriginalLines = LineDiff.SplitLines(original);
        ModifiedLines = LineDiff.SplitLines(modified);
        Language = Languages.FromFileName(path);
        Hunks = LineDiff.Compute(OriginalLines, ModifiedLines);
        Rows = Align(OriginalLines.Count, ModifiedLines.Count, Hunks);
    }

    public override string Title => $"{Path.GetFileName(_filePath)} ({ModifiedLabel})";

    public override string Description => $"{_filePath} ({OriginalLabel} ↔ {ModifiedLabel})";

    public override string? FilePath
    {
        get => _filePath;
        set => SetProperty(ref _filePath, value);
    }

    /// <summary>"HEAD", "Index"…: what the left side shows.</summary>
    public string OriginalLabel { get; }

    public string ModifiedLabel { get; }

    public IReadOnlyList<string> OriginalLines { get; }

    public IReadOnlyList<string> ModifiedLines { get; }

    public LanguageDefinition Language { get; }

    public IReadOnlyList<DiffHunk> Hunks { get; }

    public IReadOnlyList<DiffRow> Rows { get; }

    /// <summary>Row indices where each hunk starts, for "next change" navigation.</summary>
    public IEnumerable<int> HunkStartRows => Rows.Select((row, index) => (row, index))
        .Where(r => r.row.Kind != DiffRowKind.Unchanged && (r.index == 0 || Rows[r.index - 1].Kind == DiffRowKind.Unchanged))
        .Select(r => r.index);

    /// <summary>
    /// Pairs the two texts line by line: unchanged lines side by side, and inside each hunk the first
    /// lines of both sides paired as modified, the rest shown against a filler.
    /// </summary>
    public static IReadOnlyList<DiffRow> Align(int oldCount, int newCount, IReadOnlyList<DiffHunk> hunks)
    {
        var rows = new List<DiffRow>(Math.Max(oldCount, newCount));
        int oldLine = 0, newLine = 0;
        foreach (var hunk in hunks.Append(new DiffHunk(oldCount, 0, newCount, 0)))
        {
            while (oldLine < hunk.OldStart && newLine < hunk.NewStart)
            {
                rows.Add(new DiffRow(oldLine++, newLine++, DiffRowKind.Unchanged));
            }

            var paired = Math.Min(hunk.OldCount, hunk.NewCount);
            for (var i = 0; i < Math.Max(hunk.OldCount, hunk.NewCount); i++)
            {
                rows.Add(i < paired
                    ? new DiffRow(oldLine++, newLine++, DiffRowKind.Modified)
                    : i < hunk.OldCount
                        ? new DiffRow(oldLine++, null, DiffRowKind.Removed)
                        : new DiffRow(null, newLine++, DiffRowKind.Added));
            }
        }

        return rows;
    }
}
