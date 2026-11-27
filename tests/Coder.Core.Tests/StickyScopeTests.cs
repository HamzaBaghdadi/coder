using Coder.Core.Editing;
using Coder.Core.Lsp;
using Coder.Core.Syntax;
using Coder.Core.Text;

namespace Coder.Core.Tests;

public sealed class StickyScopeTests
{
    private const string CSharp = """
        namespace Demo
        {
            public class Calculator
            {
                public int Add(int a, int b)
                {
                    if (a > 0)
                    {
                        return a + b; // }
                    }

                    var s = "}{";
                    return b;
                }
            }
        }
        """;

    [Fact]
    public void Allman_braces_use_the_line_above_as_the_header()
    {
        var index = Compute(CSharp, "x.cs");

        Assert.Equal(
            [new ScopeRange(0, 14), new ScopeRange(2, 13), new ScopeRange(4, 12), new ScopeRange(6, 8)],
            index.Ranges);
    }

    [Fact]
    public void Headers_stack_outermost_first_for_the_line_at_the_top()
    {
        var index = Compute(CSharp, "x.cs");

        // Line 8 ("return a + b;") at the top: namespace, class, method and if are pinned; each pinned
        // header covers one line, so the decision moves down with them.
        Assert.Equal([0, 2, 4], index.StickyHeaders(8, 5).Select(r => r.StartLine));
        Assert.Equal([0, 2, 4, 6], index.StickyHeaders(5, 5).Select(r => r.StartLine));
        Assert.Equal([0, 2], index.StickyHeaders(5, 2).Select(r => r.StartLine)); // capped
        Assert.Empty(index.StickyHeaders(0, 5)); // the header itself is visible
    }

    [Fact]
    public void Brackets_in_strings_and_comments_are_ignored()
    {
        var index = Compute("function f() {\n  const s = '{[';\n  /* { */\n  return `}`;\n}\n", "x.ts");

        Assert.Equal([new ScopeRange(0, 3)], index.Ranges);
    }

    [Fact]
    public void Kr_braces_and_json_arrays_start_scopes_on_their_own_line()
    {
        var ts = Compute("class A {\n  m() {\n    x();\n  }\n}\n", "x.ts");
        Assert.Equal([new ScopeRange(0, 3), new ScopeRange(1, 2)], ts.Ranges);

        var json = Compute("{\n  \"items\": [\n    {\n      \"a\": 1\n    }\n  ]\n}\n", "x.json");
        Assert.Equal([new ScopeRange(0, 5), new ScopeRange(1, 4), new ScopeRange(2, 3)], json.Ranges);
        Assert.Equal([0, 1], json.StickyHeaders(3, 5).Select(r => r.StartLine));
    }

    [Fact]
    public void Markup_uses_indentation()
    {
        var html = Compute("<html>\n  <body>\n    <div>\n      <p>hi</p>\n    </div>\n  </body>\n</html>\n", "x.html");

        Assert.Equal([new ScopeRange(0, 5), new ScopeRange(1, 4), new ScopeRange(2, 3)], html.Ranges);
    }

    [Fact]
    public void Indentation_blocks_skip_blank_lines()
    {
        var python = Compute("class A:\n    def f(self):\n        x = 1\n\n        return x\n\ny = 2\n", "x.py");

        Assert.Equal([new ScopeRange(0, 4), new ScopeRange(1, 4)], python.Ranges);
        Assert.Equal([0, 1], python.StickyHeaders(3, 5).Select(r => r.StartLine));
    }

    [Fact]
    public void Language_server_symbols_merge_with_brackets()
    {
        var document = new TextDocument(CSharp);
        var method = Symbol(4, 13, []);
        var symbols = StickyScopes.FromSymbols([Symbol(2, 14, [method])], document.LineCount, document.GetLineText);

        Assert.Equal([new ScopeRange(2, 13), new ScopeRange(4, 12)], symbols);

        var index = StickyScopes.Compute(document, Languages.FromFileName("x.cs"), 4, symbols);
        Assert.Equal([0, 2, 4, 6], index.Ranges.Select(r => r.StartLine)); // one scope per header, the widest
        Assert.Equal(new ScopeRange(4, 12), index.Ranges[2]);
    }

    [Fact]
    public void Lookups_are_fast_on_large_files()
    {
        var text = new System.Text.StringBuilder();
        for (var i = 0; i < 20_000; i++)
        {
            text.Append("function f").Append(i).Append("() {\n  if (x) {\n    y();\n  }\n}\n");
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var index = Compute(text.ToString(), "x.js");
        Assert.Equal(40_000, index.Ranges.Count);
        for (var line = 0; line < 100_000; line++)
        {
            index.StickyHeaders(line, 5);
        }

        Assert.True(watch.ElapsedMilliseconds < 5_000, $"took {watch.ElapsedMilliseconds} ms");
        Assert.Equal([5 * 123, 5 * 123 + 1], index.StickyHeaders(5 * 123 + 1, 5).Select(r => r.StartLine));
    }

    [Fact]
    public void Unbalanced_code_does_not_throw()
    {
        var index = Compute("}\n]\nclass A {\n  void f() {\n", "x.cs");

        Assert.Empty(index.Ranges);
    }

    private static ScopeIndex Compute(string text, string fileName) =>
        StickyScopes.Compute(new TextDocument(text), Languages.FromFileName(fileName), 4);

    private static DocumentSymbol Symbol(int nameLine, int endLine, IReadOnlyList<DocumentSymbol> children) =>
        new("s", null, SymbolKind.Method, new TextRange(new TextPosition(nameLine, 0), new TextPosition(endLine, 1)),
            new TextRange(new TextPosition(nameLine, 4), new TextPosition(nameLine, 5)), children);
}
