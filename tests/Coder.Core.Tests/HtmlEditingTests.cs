using Coder.Core.Editing;
using Coder.Core.Syntax;
using Coder.Core.Text;

namespace Coder.Core.Tests;

/// <summary>Regression: typing after &lt;/html&gt; and deleting it froze the editor.</summary>
public sealed class HtmlEditingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Typing_after_the_closing_html_tag_and_deleting_it_finishes(bool forwardDelete)
    {
        var work = Task.Run(() =>
        {
            const string Html = "<!DOCTYPE html>\n<html>\n<head><title>x</title></head>\n<body>\n<p>hi</p>\n</body>\n</html>";
            var document = new TextDocument(Html);
            var language = Languages.FromFileName("index.html");
            var editor = new EditorState(document, language);
            var highlighter = new SyntaxHighlighter(document, language);
            var tokens = new List<Token>();
            void HighlightAll()
            {
                for (var line = 0; line < document.LineCount; line++)
                {
                    highlighter.GetTokens(line, tokens);
                }
            }

            HighlightAll();
            editor.SetCaret(document.Length);
            editor.TypeText("hello");
            HighlightAll();
            if (forwardDelete)
            {
                editor.SetCaret(document.Length - 5);
            }

            for (var i = 0; i < 5; i++)
            {
                if (forwardDelete)
                {
                    editor.DeleteForward();
                }
                else
                {
                    editor.Backspace();
                }

                HighlightAll();
            }

            return document.GetText();
        }, TestContext.Current.CancellationToken);

        var finished = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.True(finished == work, "editing hung");
        Assert.EndsWith("</html>", await work, StringComparison.Ordinal);
    }
}
