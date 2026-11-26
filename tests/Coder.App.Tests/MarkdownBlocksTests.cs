using Coder.Presentation.LanguageFeatures;

namespace Coder.App.Tests;

public sealed class MarkdownBlocksTests
{
    [Fact]
    public void Splits_code_fences_prose_and_rules_and_strips_inline_markup()
    {
        const string Hover = "```go\nfunc Println(a ...any)\n```\n\n---\n\nPrintln **formats** using `default` formats\nand writes.\n\n[fmt.Println on pkg.go.dev](https://pkg.go.dev/fmt#Println) \\*escaped\\*";

        var blocks = MarkdownBlocks.Parse(Hover, isMarkdown: true);

        Assert.Collection(blocks,
            code =>
            {
                Assert.Equal(MarkdownBlockKind.Code, code.Kind);
                Assert.Equal("go", code.Language);
                Assert.Equal("func Println(a ...any)", code.Text);
            },
            rule => Assert.Equal(MarkdownBlockKind.Rule, rule.Kind),
            prose => Assert.Equal("Println formats using default formats and writes.", prose.Text),
            link => Assert.Equal("fmt.Println on pkg.go.dev *escaped*", link.Text));
    }

    [Fact]
    public void Plain_text_stays_as_is()
    {
        var block = Assert.Single(MarkdownBlocks.Parse("a **b**\n\nc", isMarkdown: false));
        Assert.Equal("a **b**\n\nc", block.Text);
    }
}
