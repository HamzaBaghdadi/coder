using Coder.Core.Editing;
using Coder.Core.Search;
using Coder.Core.Syntax;
using Coder.Core.Text;

namespace Coder.Core.Tests;

public sealed class TextSearchTests
{
    [Fact]
    public void Plain_search_ignores_case_by_default()
    {
        var result = TextSearch.FindAll("Foo foo FOO", "foo", new SearchOptions());

        Assert.Equal([0, 4, 8], result.Matches.Select(m => m.Start));
    }

    [Fact]
    public void Whole_word_skips_partial_matches()
    {
        var result = TextSearch.FindAll("cat concat cat_x cat", "cat", new SearchOptions(WholeWord: true));

        Assert.Equal([0, 17], result.Matches.Select(m => m.Start));
    }

    [Fact]
    public void Invalid_regex_reports_an_error_instead_of_throwing()
    {
        var result = TextSearch.FindAll("abc", "(", new SearchOptions(UseRegex: true));

        Assert.Empty(result.Matches);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Replace_all_expands_regex_groups_in_one_undo_step()
    {
        var document = new TextDocument("let a = 1;\nlet b = 2;");
        var editor = new EditorState(document, Languages.FromFileName("x.js"));
        var options = new SearchOptions(UseRegex: true);
        var text = document.GetText();
        var result = TextSearch.FindAll(text, @"let (\w)", options);

        editor.ReplaceMatches(result.Matches, TextSearch.CreateReplacer(text, @"let (\w)", "const $1", options));

        Assert.Equal("const a = 1;\nconst b = 2;", document.GetText());
        editor.Undo();
        Assert.Equal("let a = 1;\nlet b = 2;", document.GetText());
    }
}
