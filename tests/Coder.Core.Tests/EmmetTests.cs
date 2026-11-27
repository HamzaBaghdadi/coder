using Coder.Core.Emmet;
using Coder.Core.Lsp;
using Coder.Core.Text;

namespace Coder.Core.Tests;

public sealed class EmmetTests
{
    private static string Expand(string abbreviation) =>
        EmmetEngine.Expand(abbreviation, new EmmetOptions("  ")) ?? throw new InvalidOperationException($"'{abbreviation}' did not expand");

    // Elements, classes, ids, attributes, text -----------------------------------------------------

    [Theory]
    [InlineData("div", "<div>${1}</div>")]
    [InlineData("p", "<p>${1}</p>")]
    [InlineData("my-element", "<my-element>${1}</my-element>")]
    [InlineData(".flex.flex-col", "<div class=\"flex flex-col\">${1}</div>")]
    [InlineData("#main", "<div id=\"main\">${1}</div>")]
    [InlineData("section#hero.dark.full", "<section id=\"hero\" class=\"dark full\">${1}</section>")]
    [InlineData("span.md:flex", "<span class=\"md:flex\">${1}</span>")]
    [InlineData("div[title=\"Hello world\" data-id=3]", "<div title=\"Hello world\" data-id=\"3\">${1}</div>")]
    [InlineData("div[data-x='a b' hidden.]", "<div data-x=\"a b\" hidden>${1}</div>")]
    [InlineData("td[colspan]", "<td colspan=\"${1}\">${2}</td>")]
    [InlineData("p{Hello}", "<p>Hello</p>")]
    [InlineData("{just text}", "just text")]
    [InlineData("p{costs \\$5}", "<p>costs \\$5</p>")]
    [InlineData("p{a } b}", null)]
    public void Expands_elements(string abbreviation, string? expected)
    {
        if (expected is null)
        {
            Assert.Null(EmmetEngine.Expand(abbreviation));
            return;
        }

        Assert.Equal(expected, Expand(abbreviation));
    }

    [Theory]
    [InlineData("br", "<br>")]
    [InlineData("hr", "<hr>")]
    [InlineData("img", "<img src=\"${1}\" alt=\"${2}\">")]
    [InlineData("div/", "<div>")]
    [InlineData("meta", "<meta>")]
    public void Void_elements_have_no_closing_tag(string abbreviation, string expected) =>
        Assert.Equal(expected, Expand(abbreviation));

    // Operators -------------------------------------------------------------------------------------

    [Fact]
    public void Child_operator_nests_and_indents_block_elements() =>
        Assert.Equal("<div>\n  <section>${1}</section>\n</div>", Expand("div>section"));

    [Fact]
    public void Inline_children_stay_on_the_line() =>
        Assert.Equal("<p><span>${1}</span></p>", Expand("p>span"));

    [Fact]
    public void Sibling_operator_puts_block_elements_on_their_own_lines() =>
        Assert.Equal("<header>${1}</header>\n<main>${2}</main>\n<footer>${3}</footer>", Expand("header+main+footer"));

    [Fact]
    public void Climb_up_operator_returns_to_the_parent_level() =>
        Assert.Equal("<div>\n  <p>${1}</p>\n</div>\n<footer>${2}</footer>", Expand("div>p^footer"));

    [Fact]
    public void Double_climb_goes_up_two_levels() =>
        Assert.Equal("<main>\n  <div>\n    <p>${1}</p>\n  </div>\n</main>\n<aside>${2}</aside>", Expand("main>div>p^^aside"));

    [Fact]
    public void Multiplication_repeats_with_numbering()
    {
        Assert.Equal(
            "<ul>\n  <li class=\"item1\">${1}</li>\n  <li class=\"item2\">${2}</li>\n  <li class=\"item3\">${3}</li>\n</ul>",
            Expand("ul>li.item$*3"));
    }

    [Theory]
    [InlineData("h$*3", "<h1>${1}</h1>\n<h2>${2}</h2>\n<h3>${3}</h3>")]
    [InlineData("p.n$$$*2", "<p class=\"n001\">${1}</p>\n<p class=\"n002\">${2}</p>")]
    [InlineData("p.n$@-*3", "<p class=\"n3\">${1}</p>\n<p class=\"n2\">${2}</p>\n<p class=\"n1\">${3}</p>")]
    [InlineData("p.n$@5*2", "<p class=\"n5\">${1}</p>\n<p class=\"n6\">${2}</p>")]
    [InlineData("p{Item $}*2", "<p>Item 1</p>\n<p>Item 2</p>")]
    [InlineData("div.single$", "<div class=\"single1\">${1}</div>")]
    public void Numbering_variants(string abbreviation, string expected) =>
        Assert.Equal(expected, Expand(abbreviation));

    [Fact]
    public void Groups_repeat_and_continue_with_siblings()
    {
        Assert.Equal(
            "<div>\n  <header>${1}</header>\n  <p>${2}</p>\n</div>\n<footer>${3}</footer>",
            Expand("(div>header+p)+footer"));
        Assert.Equal(
            "<dl>\n  <dt>${1}</dt>\n  <dd>${2}</dd>\n  <dt>${3}</dt>\n  <dd>${4}</dd>\n</dl>",
            Expand("dl>(dt+dd)*2"));
    }

    [Fact]
    public void Repeated_items_number_inside_their_children() =>
        Assert.Equal(
            "<ul>\n  <li><a href=\"#s1\">Section 1</a></li>\n  <li><a href=\"#s2\">Section 2</a></li>\n</ul>",
            Expand("ul>li*2>a[href=#s$]{Section $}"));

    [Fact]
    public void Three_inline_siblings_break_onto_lines() =>
        Assert.Equal("<span>${1}</span>\n<span>${2}</span>\n<span>${3}</span>", Expand("span*3"));

    [Fact]
    public void Two_inline_siblings_stay_together() =>
        Assert.Equal("<p><b>${1}</b><i>${2}</i></p>", Expand("p>b+i"));

    // Implicit tag names ----------------------------------------------------------------------------

    [Theory]
    [InlineData("ul>.x", "<ul>\n  <li class=\"x\">${1}</li>\n</ul>")]
    [InlineData("ol>.x", "<ol>\n  <li class=\"x\">${1}</li>\n</ol>")]
    [InlineData("table>.row>.cell", "<table>\n  <tr class=\"row\">\n    <td class=\"cell\">${1}</td>\n  </tr>\n</table>")]
    [InlineData("select>.o", "<select name=\"${1}\" id=\"${1}\">\n  <option class=\"o\">${2}</option>\n</select>")]
    [InlineData("em>.x", "<em><span class=\"x\">${1}</span></em>")]
    [InlineData("section>.x", "<section>\n  <div class=\"x\">${1}</div>\n</section>")]
    public void Implicit_tags_follow_the_parent(string abbreviation, string expected) =>
        Assert.Equal(expected, Expand(abbreviation));

    // Snippets ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("a", "<a href=\"${1}\">${2}</a>")]
    [InlineData("a:blank", "<a href=\"http://${1}\" target=\"_blank\" rel=\"noopener noreferrer\">${2}</a>")]
    [InlineData("inp", "<input type=\"text\" name=\"${1}\" id=\"${1}\">")]
    [InlineData("input", "<input type=\"${1:text}\">")]
    [InlineData("input:b", "<input type=\"button\" value=\"${1}\">")]
    [InlineData("input:btn", "<input type=\"button\" value=\"${1}\">")]
    [InlineData("input:checkbox", "<input type=\"checkbox\" name=\"${1}\" id=\"${1}\">")]
    [InlineData("input:c", "<input type=\"checkbox\" name=\"${1}\" id=\"${1}\">")]
    [InlineData("input:email", "<input type=\"email\" name=\"${1}\" id=\"${1}\">")]
    [InlineData("input:datetime-local", "<input type=\"datetime-local\" name=\"${1}\" id=\"${1}\">")]
    [InlineData("input:h", "<input type=\"hidden\" name=\"${1}\">")]
    [InlineData("link:css", "<link rel=\"stylesheet\" href=\"${1:style}.css\">")]
    [InlineData("script:src", "<script src=\"${1}\">${2}</script>")]
    [InlineData("btn", "<button>${1}</button>")]
    [InlineData("btn:s", "<button type=\"submit\">${1}</button>")]
    [InlineData("btn:d", "<button disabled>${1}</button>")]
    [InlineData("textarea", "<textarea name=\"${1}\" id=\"${1}\" cols=\"${2:30}\" rows=\"${3:10}\">${4}</textarea>")]
    [InlineData("meta:vp", "<meta name=\"viewport\" content=\"width=${1:device-width}, initial-scale=${2:1.0}\">")]
    [InlineData("c", "<!-- ${1} -->")]
    [InlineData("bq", "<blockquote>${1}</blockquote>")]
    public void Expands_html_snippets(string abbreviation, string expected) =>
        Assert.Equal(expected, Expand(abbreviation));

    [Fact]
    public void Snippets_merge_with_typed_attributes_and_children()
    {
        Assert.Equal("<a href=\"${1}\" class=\"btn\">${2}</a>", Expand("a.btn"));
        Assert.Equal("<a href=\"/home\">Home</a>", Expand("a[href=/home]{Home}"));
        Assert.Equal("<input type=\"email\" name=\"${1}\" id=\"${1}\" class=\"field\" required>", Expand("input:email.field[required.]"));
        Assert.Equal("<ul>\n  <li>${1}</li>\n</ul>", Expand("ul+"));
        Assert.Equal("<button type=\"submit\"><span>${1}</span></button>", Expand("btn:s>span"));
    }

    [Fact]
    public void Repeated_snippets_get_separate_fields() =>
        Assert.Equal(
            "<div><input type=\"text\" name=\"${1}\" id=\"${1}\"><input type=\"text\" name=\"${2}\" id=\"${2}\"></div>",
            Expand("div>inp*2"));

    [Fact]
    public void Exclamation_mark_is_the_html_boilerplate()
    {
        const string expected =
            "<!DOCTYPE html>\n" +
            "<html lang=\"en\">\n" +
            "<head>\n" +
            "  <meta charset=\"UTF-8\">\n" +
            "  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">\n" +
            "  <title>${1:Document}</title>\n" +
            "</head>\n" +
            "<body>\n" +
            "  ${2}\n" +
            "</body>\n" +
            "</html>";
        Assert.Equal(expected, Expand("!"));
        Assert.Equal(expected, Expand("html:5"));
    }

    [Fact]
    public void Boilerplate_takes_children_into_the_body() =>
        Assert.EndsWith("<body>\n  <main>${2}</main>\n</body>\n</html>", Expand("!>main"), StringComparison.Ordinal);

    [Fact]
    public void Uses_the_given_indentation() =>
        Assert.Equal("<ul>\n\t<li>${1}</li>\n</ul>", EmmetEngine.Expand("ul>li"));

    [Fact]
    public void Expansion_is_a_working_snippet()
    {
        var expansion = Snippet.Expand(Expand("ul>li.item$*2>a"));

        Assert.Equal("<ul>\n  <li class=\"item1\"><a href=\"\"></a></li>\n  <li class=\"item2\"><a href=\"\"></a></li>\n</ul>", expansion.Text);
        Assert.Equal(expansion.Text.IndexOf("href=\"", StringComparison.Ordinal) + 6, expansion.SelectionStart); // first stop: the first href
        Assert.Equal(4, expansion.TabStops.Select(s => s.Number).Distinct().Count());
    }

    // Invalid input ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("ul>")]
    [InlineData("ul>li+")]
    [InlineData(".")]
    [InlineData("#")]
    [InlineData("a[")]
    [InlineData("a[href")]
    [InlineData("p{open")]
    [InlineData("(div")]
    [InlineData("div)")]
    [InlineData("3d")]
    [InlineData("a b")]
    public void Malformed_abbreviations_do_not_expand(string abbreviation) =>
        Assert.Null(EmmetEngine.Expand(abbreviation));

    [Theory]
    [InlineData("div", true)]
    [InlineData("inp", true)]
    [InlineData("!", true)]
    [InlineData(".x", true)]
    [InlineData("ul>li", true)]
    [InlineData("foo*2", true)]
    [InlineData("hello", false)] // a plain word that is no element: prose, not an abbreviation
    [InlineData("ul>", false)]
    public void Validity(string abbreviation, bool valid) => Assert.Equal(valid, EmmetEngine.IsValid(abbreviation));

    // Extraction and context ----------------------------------------------------------------------

    [Theory]
    [InlineData("inp", "inp")]
    [InlineData("    ul>li*3", "ul>li*3")]
    [InlineData("text ul>li", "ul>li")]
    [InlineData("<div>ul>li", "ul>li")]
    [InlineData("<div class=\"a\">.flex.flex-col", ".flex.flex-col")]
    [InlineData("</p>a", "a")]
    [InlineData("x a[title=\"two words\"]", "a[title=\"two words\"]")]
    [InlineData("p{hello world}", "p{hello world}")]
    [InlineData("(header>nav)+main", "(header>nav)+main")]
    [InlineData("p{hello w", "w")] // inside unclosed text only the last word counts
    [InlineData("a[title=x ", null)]
    [InlineData("    ", null)]
    public void Extracts_the_abbreviation_before_the_caret(string line, string? expected) =>
        Assert.Equal(expected, EmmetEngine.Extract(line)?.Text);

    [Theory]
    [InlineData("", true)]
    [InlineData("<div>\n  ", true)]
    [InlineData("<p>Some text ", true)]
    [InlineData("<div class=\"", false)]
    [InlineData("<div\n  class=\"x\" ", false)]
    [InlineData("<!-- note ", false)]
    [InlineData("<!-- note --> ", true)]
    [InlineData("<script>\n  ", false)]
    [InlineData("<script src=\"a.js\"></script>\n", true)]
    [InlineData("<style>\n", false)]
    [InlineData("if (a < b) ", true)]
    public void Knows_markup_context(string textBefore, bool expected) =>
        Assert.Equal(expected, EmmetEngine.IsMarkupContext(textBefore));

    // Completion items ------------------------------------------------------------------------------

    [Fact]
    public void Completes_inp_like_vs_code()
    {
        var result = EmmetEngine.Complete("<body>\n  inp", new TextPosition(1, 5));

        Assert.NotNull(result.Abbreviation);
        var labels = result.Items.Select(i => i.Label).ToList();
        Assert.Equal("inp", labels[0]);
        Assert.Contains("input", labels);
        Assert.Contains("input:b", labels);
        Assert.Contains("input:btn", labels);
        Assert.Contains("input:checkbox", labels);
        Assert.Contains("input:color", labels);
        Assert.Contains("input:date", labels);
        Assert.All(result.Items, item =>
        {
            Assert.Equal(EmmetEngine.ItemDetail, item.Detail);
            Assert.True(item.IsSnippet);
            Assert.Equal(new TextRange(new TextPosition(1, 2), new TextPosition(1, 5)), item.TextEdit!.Range);
        });
        Assert.Equal("<input type=\"text\" name=\"|\" id=\"|\">", result.Abbreviation!.Documentation);
    }

    [Fact]
    public void Completes_an_operator_abbreviation_with_its_whole_range()
    {
        var result = EmmetEngine.Complete("<div>ul>li.a", new TextPosition(0, 12));

        var item = Assert.Single(result.Items);
        Assert.Equal("ul>li.a", item.Label);
        Assert.Equal(5, item.TextEdit!.Range.Start.Column);
    }

    [Theory]
    [InlineData("<div class=\"inp")]
    [InlineData("<script>\ninp")]
    [InlineData("hello")]
    public void Offers_nothing_outside_markup_or_for_prose(string textBefore)
    {
        var lines = textBefore.Split('\n');
        var result = EmmetEngine.Complete(textBefore, new TextPosition(lines.Length - 1, lines[^1].Length));

        Assert.Null(result.Abbreviation);
        Assert.Empty(result.Items);
    }

    [Fact]
    public void Preview_marks_tab_stops() =>
        Assert.Equal("<a href=\"|\">|</a>", EmmetEngine.Preview(Expand("a")));
}
