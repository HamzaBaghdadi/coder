using Coder.Presentation.Agent;

namespace Coder.App.Tests;

public sealed class ChatMarkdownTests
{
    [Fact]
    public void Blocks_cover_what_chat_models_write()
    {
        var blocks = ChatMarkdown.Parse("""
            # Title
            Some **bold** and *italic* text,
            continued on the next line.

            1. First
            2. Second
               - Nested with `code`
            > A quote
            > over two lines

            | Name | Value |
            |------|------:|
            | a    | 1     |

            ---
            ```python
            print("hi")
            ```
            """);

        Assert.Equal(
            [ChatBlockKind.Heading, ChatBlockKind.Paragraph, ChatBlockKind.ListItem, ChatBlockKind.ListItem, ChatBlockKind.ListItem, ChatBlockKind.Quote, ChatBlockKind.Table, ChatBlockKind.Rule, ChatBlockKind.Code],
            blocks.Select(b => b.Kind));
        Assert.Equal(1, blocks[0].Level);
        Assert.Equal("Some bold and italic text, continued on the next line.", blocks[1].PlainText);
        Assert.Equal([InlineKind.Text, InlineKind.Bold, InlineKind.Text, InlineKind.Italic, InlineKind.Text], blocks[1].Spans.Select(s => s.Kind));
        Assert.Equal(("1.", 0), (blocks[2].Marker, blocks[2].Level));
        Assert.Equal(("•", 1), (blocks[4].Marker, blocks[4].Level));
        Assert.Equal(InlineKind.Code, blocks[4].Spans[^1].Kind);
        Assert.Equal("A quote over two lines", blocks[5].PlainText);
        Assert.Equal(2, blocks[6].Rows.Count);
        Assert.Equal("Value", blocks[6].Rows[0][1][0].Text);
        Assert.Equal(("print(\"hi\")", "python", false), (blocks[8].Text, blocks[8].Language, blocks[8].IsOpen));
    }

    [Fact]
    public void Half_written_input_from_a_stream_parses()
    {
        var blocks = ChatMarkdown.Parse("Here:\n```cs\nvar a = 1;\nvar b");
        Assert.Equal(ChatBlockKind.Code, blocks[^1].Kind);
        Assert.True(blocks[^1].IsOpen);
        Assert.Equal("var a = 1;\nvar b", blocks[^1].Text);

        // Unclosed emphasis stays text; snake_case is not italic; links keep their address.
        var spans = ChatMarkdown.Inline("a **b and my_var_name see [docs](https://example.com) \\*x\\*");
        Assert.Equal("a **b and my_var_name see ", spans[0].Text);
        Assert.Equal(new InlineSpan(InlineKind.Link, "docs", "https://example.com"), spans[1]);
        Assert.Equal(" *x*", spans[2].Text);
    }

    [Fact]
    public void Reasoning_in_think_tags_is_kept_out_of_the_reply()
    {
        var message = new AssistantMessageItem();
        message.AppendText("<thi");
        Assert.Equal(string.Empty, message.Text);
        message.AppendText("nk>Let me see");
        Assert.True(message.IsThinking);
        Assert.Equal("Let me see", message.Reasoning);
        message.AppendText(".</think>\n\nThe answer.");
        Assert.Equal("The answer.", message.Text);
        Assert.Equal("Let me see.", message.Reasoning);
        message.Complete();
        Assert.False(message.IsThinking);
    }

    [Fact]
    public void Unified_diff_keeps_context_and_folds_the_rest()
    {
        var before = Enumerable.Range(1, 20).Select(i => $"line {i}").ToList();
        var after = before.ToList();
        after[9] = "changed 10";
        var (lines, added, removed) = EditProposalItem.Unified(before, after);

        Assert.Equal((1, 1), (added, removed));
        Assert.Equal(
            [DiffLineKind.Gap, DiffLineKind.Context, DiffLineKind.Context, DiffLineKind.Removed, DiffLineKind.Added, DiffLineKind.Context, DiffLineKind.Context, DiffLineKind.Gap],
            lines.Select(l => l.Kind));
        Assert.Equal("7 unchanged lines", lines[0].Text);
        Assert.Equal((10, null), (lines[3].OldNumber, lines[3].NewNumber));
        Assert.Equal((null, 10), (lines[4].OldNumber, lines[4].NewNumber));
        Assert.Equal("8 unchanged lines", lines[^1].Text);

        var (created, count, _) = EditProposalItem.Unified([], ["a", "b"]);
        Assert.Equal(2, count);
        Assert.All(created, l => Assert.Equal(DiffLineKind.Added, l.Kind));
    }

    [Fact]
    public void Accepted_edits_replace_only_what_changed()
    {
        Assert.Equal((6, 7, "X"), AgentViewModel.MinimalEdit("hello world", "hello Xorld"));
        Assert.Equal((5, 5, ", there"), AgentViewModel.MinimalEdit("hello", "hello, there"));
        Assert.Equal((0, 3, string.Empty), AgentViewModel.MinimalEdit("abc", string.Empty));
        Assert.Equal((2, 2, string.Empty), AgentViewModel.MinimalEdit("aa", "aa"));
    }
}
