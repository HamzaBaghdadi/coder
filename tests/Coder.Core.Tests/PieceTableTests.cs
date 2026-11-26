using System.Text;
using Coder.Core.Text;

namespace Coder.Core.Tests;

public sealed class PieceTableTests
{
    [Fact]
    public void Lines_are_indexed_from_original_text()
    {
        var table = new PieceTable("one\ntwo\n\nfour");

        Assert.Equal(4, table.LineCount);
        Assert.Equal("one", table.GetLineText(0));
        Assert.Equal("two", table.GetLineText(1));
        Assert.Equal(string.Empty, table.GetLineText(2));
        Assert.Equal("four", table.GetLineText(3));
        Assert.Equal(9, table.GetLineStart(3));
    }

    [Fact]
    public void Sequential_typing_reuses_a_single_piece()
    {
        var table = new PieceTable("ab");
        table.Insert(1, "x");
        var piecesAfterFirstInsert = table.PieceCount;

        table.Insert(2, "y");
        table.Insert(3, "z");

        Assert.Equal("axyzb", table.GetText());
        Assert.Equal(piecesAfterFirstInsert, table.PieceCount);
    }

    [Fact]
    public void Delete_across_pieces_keeps_line_index_consistent()
    {
        var table = new PieceTable("hello\nworld");
        table.Insert(5, "!\nnew line");
        // "hello!\nnew line\nworld" minus "lo!\nnew li"
        table.Delete(3, 10);

        Assert.Equal("helne\nworld", table.GetText());
        Assert.Equal(2, table.LineCount);
        Assert.Equal("world", table.GetLineText(1));
    }

    [Fact]
    public void Random_edits_match_a_reference_string()
    {
        var random = new Random(1234);
        var reference = new StringBuilder("seed\ntext\n");
        var table = new PieceTable(reference.ToString());
        string[] fragments = ["a", "bc", "\n", "x\ny", "\n\n", "longer fragment ", "é😀"];

        for (var step = 0; step < 3000; step++)
        {
            if (reference.Length > 0 && random.Next(3) == 0)
            {
                var offset = random.Next(reference.Length);
                var length = random.Next(Math.Min(8, reference.Length - offset) + 1);
                reference.Remove(offset, length);
                table.Delete(offset, length);
            }
            else
            {
                var offset = random.Next(reference.Length + 1);
                var text = fragments[random.Next(fragments.Length)];
                reference.Insert(offset, text);
                table.Insert(offset, text);
            }

            if (step % 50 == 0)
            {
                AssertEquivalent(reference.ToString(), table);
            }
        }

        AssertEquivalent(reference.ToString(), table);
    }

    private static void AssertEquivalent(string expected, PieceTable table)
    {
        Assert.Equal(expected, table.GetText());
        var lines = expected.Split('\n');
        Assert.Equal(lines.Length, table.LineCount);

        var offset = 0;
        for (var line = 0; line < lines.Length; line++)
        {
            Assert.Equal(offset, table.GetLineStart(line));
            Assert.Equal(lines[line], table.GetLineText(line));
            Assert.Equal(line, table.GetLineOfOffset(offset));
            offset += lines[line].Length + 1;
        }
    }
}
