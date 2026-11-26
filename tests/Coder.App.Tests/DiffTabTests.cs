using Coder.Presentation.Editors;

namespace Coder.App.Tests;

public sealed class DiffTabTests
{
    [Fact]
    public void Rows_pair_changed_lines_and_pad_the_shorter_side()
    {
        var diff = new DiffTabViewModel("a.cs", "Index", "one\ntwo\nthree\nfour", "Working Tree", "one\nTWO\n2.5\nthree");

        Assert.Equal(
        [
            new DiffRow(0, 0, DiffRowKind.Unchanged),
            new DiffRow(1, 1, DiffRowKind.Modified),   // two → TWO, paired
            new DiffRow(null, 2, DiffRowKind.Added),   // 2.5 has no partner
            new DiffRow(2, 3, DiffRowKind.Unchanged),
            new DiffRow(3, null, DiffRowKind.Removed), // four was deleted
        ], diff.Rows);
        Assert.Equal([1, 4], diff.HunkStartRows);
        Assert.Equal("a.cs (Working Tree)", diff.Title);
    }

    [Fact]
    public void Identical_texts_have_only_unchanged_rows()
    {
        var diff = new DiffTabViewModel("a.cs", "HEAD", "x\ny", "Index", "x\ny");

        Assert.All(diff.Rows, row => Assert.Equal(DiffRowKind.Unchanged, row.Kind));
        Assert.Empty(diff.HunkStartRows);
    }
}
