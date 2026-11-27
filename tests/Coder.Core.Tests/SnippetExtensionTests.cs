using Coder.Core.Extensions;
using Coder.Core.Lsp;
using Coder.Core.Snippets;

namespace Coder.Core.Tests;

/// <summary>Snippets contributed by extensions: VS Code's file format, the manifest, languages and variables.</summary>
public sealed class SnippetExtensionTests
{
    private static string? Variables(string name) => name switch
    {
        "TM_FILENAME_BASE" => "user-profile_card",
        "TM_FILENAME" => "user-profile_card.tsx",
        "CURRENT_YEAR" => "2026",
        "TM_SELECTED_TEXT" => string.Empty,
        _ => null,
    };

    [Theory]
    [InlineData("$TM_FILENAME", "user-profile_card.tsx")]
    [InlineData("${TM_FILENAME_BASE}", "user-profile_card")]
    [InlineData("${TM_SELECTED_TEXT:fallback}", "fallback")]                          // empty: the default
    [InlineData("${CURRENT_YEAR:1999}", "2026")]                                       // known: the default is dropped
    [InlineData("$NOT_A_VARIABLE", "NOT_A_VARIABLE")]                                 // unknown: its name, like VS Code
    [InlineData("${TM_FILENAME_BASE/(.*)/${1:/upcase}/}", "USER-PROFILE_CARD")]
    [InlineData("${TM_FILENAME_BASE/(.*)/${1:/pascalcase}/}", "UserProfileCard")]
    [InlineData("${TM_FILENAME_BASE/(.*)/${1:/camelcase}/}", "userProfileCard")]
    [InlineData("${TM_FILENAME_BASE/(.*)/${1:/capitalize}/}", "User-profile_card")]
    [InlineData("${TM_FILENAME_BASE/[-_]/./g}", "user.profile.card")]                // global replace
    [InlineData("${TM_FILENAME_BASE/[-_]/./}", "user.profile_card")]                 // first match only
    [InlineData("${TM_FILENAME/(.*)\\.tsx$/$1/}", "user-profile_card")]
    [InlineData("${TM_FILENAME_BASE/(card)?$/${1:?has card:no card}/}", "user-profile_has card")]
    [InlineData("${TM_FILENAME_BASE/(x)?$/${1:+yes}/}", "user-profile_card")]
    [InlineData("${TM_FILENAME_BASE/(x)?$/${1:-none}/}", "user-profile_cardnone")]
    [InlineData("\\${not} \\$x", "${not} $x")]
    public void Variables_and_transforms_expand_like_vs_code(string snippet, string text) =>
        Assert.Equal(text, Snippet.Expand(snippet, Variables).Text);

    [Fact]
    public void A_variable_inside_a_placeholder_becomes_its_text()
    {
        var expansion = Snippet.Expand("export function ${1:${TM_FILENAME_BASE/(.*)/${1:/pascalcase}/}}() {\n\t$0\n}", Variables);

        Assert.Equal("export function UserProfileCard() {\n\t\n}", expansion.Text);
        Assert.Equal(16, expansion.SelectionStart);
        Assert.Equal("UserProfileCard".Length, expansion.SelectionLength);
    }

    [Fact]
    public void Reads_vs_code_snippet_files()
    {
        var json = """
            {
              // comments and trailing commas, as VS Code allows
              "For Loop": {
                "prefix": ["for", "fori"],
                "body": ["for (var ${1:i} = 0; $1 < ${2:n}; $1++)", "{", "\t$0", "}"],
                "description": "A for loop",
              },
              "No prefix": { "body": "x" },
              "Not a snippet": 42,
            }
            """;

        var snippet = Assert.Single(SnippetFile.Parse(json, "csharp", "C# Snippets"));

        Assert.Equal("For Loop", snippet.Name);
        Assert.Equal(["for", "fori"], snippet.Prefixes);
        Assert.Equal("for (var ${1:i} = 0; $1 < ${2:n}; $1++)\n{\n\t$0\n}", snippet.Body);
        Assert.Equal("A for loop", snippet.Description);
        Assert.Equal(["csharp"], snippet.Languages);
        Assert.Equal("C# Snippets", snippet.Source);
    }

    [Fact]
    public void Code_snippets_files_carry_their_own_scope()
    {
        var json = """{ "Log": { "prefix": "log", "body": "console.log($1)", "scope": "javascript, typescript" }, "Any": { "prefix": "todo", "body": "TODO: $0" } }""";

        var snippets = SnippetFile.Parse(json, language: null, "Mine");

        Assert.Equal(["javascript", "typescript"], snippets[0].Languages);
        Assert.True(snippets[0].AppliesTo(["typescript"]));
        Assert.False(snippets[0].AppliesTo(["python"]));
        Assert.Empty(snippets[1].Languages);
        Assert.True(snippets[1].AppliesTo(["python"]));
    }

    [Theory]
    [InlineData("typescript", "C:/src/App.tsx", "typescriptreact")]
    [InlineData("typescript", "C:/src/app.ts", "typescript")]
    [InlineData("javascript", "C:/src/App.jsx", "javascriptreact")]
    [InlineData("css", "C:/src/site.scss", "scss")]
    [InlineData("csharp", "C:/src/Program.cs", "csharp")]
    [InlineData("markdown", null, "markdown")]
    public void Files_get_vs_code_language_ids(string language, string? path, string expected) =>
        Assert.Equal([expected], SnippetLanguages.For(language, path));

    [Fact]
    public void The_catalog_reads_installed_extensions_and_skips_what_it_cannot_use()
    {
        var root = Directory.CreateTempSubdirectory("coder-snippets-").FullName;
        try
        {
            var folder = Path.Combine(root, "acme.csharp-1.0.0");
            Directory.CreateDirectory(Path.Combine(folder, "snippets"));
            File.WriteAllText(Path.Combine(folder, "package.json"), """
                {
                  "publisher": "acme", "name": "csharp", "version": "1.0.0", "displayName": "Acme C#",
                  "contributes": {
                    "snippets": [
                      { "language": "csharp", "path": "./snippets/csharp.json" },
                      { "language": "razor", "path": "./snippets/broken.json" },
                      { "language": "csharp", "path": "../outside.json" },
                      { "language": "csharp", "path": "./snippets/missing.json" }
                    ]
                  }
                }
                """);
            File.WriteAllText(Path.Combine(folder, "snippets", "csharp.json"), """{ "Property": { "prefix": "prop", "body": "public ${1:int} ${2:Name} { get; set; }$0" } }""");
            File.WriteAllText(Path.Combine(folder, "snippets", "broken.json"), "{ not json");
            File.WriteAllText(Path.Combine(root, "outside.json"), """{ "Escaped": { "prefix": "esc", "body": "x" } }""");

            var store = new ExtensionStore(root, new HttpClient());
            var installed = Assert.Single(store.Installed());
            Assert.Equal(["csharp", "razor"], installed.SnippetLanguages);
            Assert.Empty(installed.Themes);

            var catalog = new SnippetCatalog(store);
            var snippet = Assert.Single(catalog.All);
            Assert.Equal("Property", snippet.Name);
            Assert.Equal("Acme C#", snippet.Source);
            Assert.Single(catalog.For(["csharp"]));
            Assert.Empty(catalog.For(["python"]));

            store.Uninstall("acme.csharp");
            Assert.Empty(catalog.All); // re-read after the store changed
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Variables_resolve_from_the_insertion_context()
    {
        var context = new SnippetContext("C:/work/app/src/user_card.tsx", "C:/work/app", "  const x = 1;", 4, "us", string.Empty, "//", ("/*", "*/"), new DateTime(2026, 11, 27, 9, 5, 7));

        Assert.Equal("user_card", SnippetVariables.Resolve("TM_FILENAME_BASE", context));
        Assert.Equal("user_card.tsx", SnippetVariables.Resolve("TM_FILENAME", context));
        Assert.Equal(Path.Combine("src", "user_card.tsx"), SnippetVariables.Resolve("RELATIVE_FILEPATH", context));
        Assert.Equal("app", SnippetVariables.Resolve("WORKSPACE_NAME", context));
        Assert.Equal("5", SnippetVariables.Resolve("TM_LINE_NUMBER", context));
        Assert.Equal("2026-11-27 09:05:07", $"{SnippetVariables.Resolve("CURRENT_YEAR", context)}-{SnippetVariables.Resolve("CURRENT_MONTH", context)}-{SnippetVariables.Resolve("CURRENT_DATE", context)} {SnippetVariables.Resolve("CURRENT_HOUR", context)}:{SnippetVariables.Resolve("CURRENT_MINUTE", context)}:{SnippetVariables.Resolve("CURRENT_SECOND", context)}");
        Assert.Equal("November", SnippetVariables.Resolve("CURRENT_MONTH_NAME", context));
        Assert.Equal("//", SnippetVariables.Resolve("LINE_COMMENT", context));
        Assert.Equal("*/", SnippetVariables.Resolve("BLOCK_COMMENT_END", context));
        Assert.Equal(string.Empty, SnippetVariables.Resolve("CLIPBOARD", context));
        Assert.Matches("^[0-9a-f-]{36}$", SnippetVariables.Resolve("UUID", context));
        Assert.Null(SnippetVariables.Resolve("NOPE", context));
    }
}
