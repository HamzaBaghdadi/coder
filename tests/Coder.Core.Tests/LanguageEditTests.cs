using System.Text.Json;
using Coder.Core.Editing;
using Coder.Core.Lsp;
using Coder.Core.Syntax;
using Coder.Core.Text;

namespace Coder.Core.Tests;

public sealed class LanguageEditTests
{
    [Fact]
    public void ApplyEdits_is_one_undo_step_and_cursors_follow_the_text()
    {
        var editor = new EditorState(new TextDocument("a := 1\nb:=2\n"), Languages.PlainText);
        editor.SetCaret(10); // just before the "2"

        editor.ApplyEdits([(8, 10, " := "), (0, 0, "// header\n")]);

        Assert.Equal("// header\na := 1\nb := 2\n", editor.Document.GetText());
        Assert.Equal(editor.Document.GetText().IndexOf("2\n", StringComparison.Ordinal), editor.Selection.Active); // still before the "2"
        editor.Undo();
        Assert.Equal("a := 1\nb:=2\n", editor.Document.GetText());
    }

    [Fact]
    public void ApplyEdits_keeps_the_order_of_insertions_at_one_offset()
    {
        var editor = new EditorState(new TextDocument("x"), Languages.PlainText);

        editor.ApplyEdits([(0, 0, "first "), (0, 0, "second ")]);

        Assert.Equal("first second x", editor.Document.GetText());
    }

    [Fact]
    public void Snippet_tab_stops_move_with_tab_and_mirrors_edit_together()
    {
        var expansion = Snippet.Expand("for ${1:i} := 0; $1 < ${2:n}; $1++ {\n\t$0\n}");
        var editor = new EditorState(new TextDocument(""), Languages.PlainText);

        editor.InsertCompletion(0, expansion.Text, expansion.SelectionStart, expansion.SelectionLength, [],
            expansion.TabStops.Select(s => (s.Number, s.Start, s.Length)).ToList());

        // $1 and its two mirrors are selected together; typing replaces all three.
        Assert.True(editor.IsInSnippet);
        Assert.Equal(3, editor.Selections.Count);
        editor.TypeText("idx");
        Assert.Equal("for idx := 0; idx < n; idx++ {\n\t\n}", editor.Document.GetText());

        // Tab: to $2 ("n"), Shift+Tab back, Tab twice to the final position, which ends the snippet.
        Assert.True(editor.MoveToTabStop(backwards: false));
        Assert.Equal("n", editor.SelectedText);
        Assert.True(editor.MoveToTabStop(backwards: true));
        Assert.Equal(3, editor.Selections.Count);
        editor.MoveToTabStop(backwards: false);
        editor.MoveToTabStop(backwards: false);
        Assert.False(editor.IsInSnippet);
        Assert.Equal(editor.Document.GetText().IndexOf("\t\n", StringComparison.Ordinal) + 1, editor.Selection.Active);

        // Without a snippet, Tab is the editor's own indent again.
        Assert.False(editor.MoveToTabStop(backwards: false));
    }

    [Fact]
    public void Leaving_the_snippet_ends_it()
    {
        var expansion = Snippet.Expand("f(${1:a}, ${2:b})");
        var editor = new EditorState(new TextDocument("x\n"), Languages.PlainText);
        editor.SetCaret(2);
        editor.InsertCompletion(2, expansion.Text, expansion.SelectionStart, expansion.SelectionLength, [],
            expansion.TabStops.Select(s => (s.Number, s.Start, s.Length)).ToList());

        editor.SetCaret(0); // clicked elsewhere
        Assert.False(editor.MoveToTabStop(backwards: false));
        Assert.False(editor.IsInSnippet);
    }

    [Fact]
    public void Parses_workspace_edits_in_both_shapes()
    {
        var uri = DocumentUri.FromPath(Path.GetFullPath("a.go"));
        var newUri = DocumentUri.FromPath(Path.GetFullPath("new.go"));
        var renamedUri = DocumentUri.FromPath(Path.GetFullPath("b.go"));
        var changes = JsonElement.Parse($$$"""{"changes":{"{{{uri}}}":[{"range":{"start":{"line":1,"character":2},"end":{"line":1,"character":8}},"newText":"compute"}]}}""");
        var documentChanges = JsonElement.Parse($$$"""
            {"documentChanges":[
              {"textDocument":{"uri":"{{{uri}}}","version":3},"edits":[{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}},"newText":"x"}]},
              {"kind":"create","uri":"{{{newUri}}}","options":{"ignoreIfExists":true}},
              {"kind":"rename","oldUri":"{{{uri}}}","newUri":"{{{renamedUri}}}","options":{"overwrite":true}},
              {"kind":"delete","uri":"{{{newUri}}}","options":{"recursive":true}},
              {"kind":"unknown"}]}
            """);

        var first = LspJson.ReadWorkspaceEdit(changes)!;
        var second = LspJson.ReadWorkspaceEdit(documentChanges)!;

        Assert.Equal("compute", Assert.Single(Assert.Single(first.Files).Edits).NewText);
        Assert.Equal(Path.GetFullPath("a.go"), Assert.Single(second.Files).Path, ignoreCase: OperatingSystem.IsWindows());

        // File operations are kept, in order, after the edit.
        Assert.Equal(4, second.Changes.Count);
        Assert.Equal(new CreateFile(Path.GetFullPath("new.go"), Overwrite: false, IgnoreIfExists: true), second.Changes[1]);
        Assert.Equal(new RenameFile(Path.GetFullPath("a.go"), Path.GetFullPath("b.go"), Overwrite: true, IgnoreIfExists: false), second.Changes[2]);
        Assert.Equal(new DeleteFile(Path.GetFullPath("new.go"), Recursive: true, IgnoreIfNotExists: false), second.Changes[3]);
        Assert.Equal(4, second.EditCount);
    }

    [Fact]
    public void Parses_signature_help_with_text_and_offset_parameter_labels()
    {
        var result = JsonElement.Parse("""
            {"activeSignature":0,"activeParameter":1,"signatures":[
              {"label":"Printf(format string, a ...any)","documentation":{"kind":"markdown","value":"Printf formats."},
               "parameters":[{"label":"format string"},{"label":[22,30]}]}]}
            """);

        var help = LspJson.ReadSignatureHelp(result)!;

        var signature = Assert.Single(help.Signatures);
        Assert.Equal([(7, 20), (22, 30)], signature.Parameters);
        Assert.Equal("a ...any", signature.Label[22..30]);
        Assert.Equal(1, help.ActiveParameter);
        Assert.Equal("Printf formats.", signature.Documentation);
    }

    [Fact]
    public void Parses_code_actions_and_bare_commands_and_drops_disabled_ones()
    {
        var result = JsonElement.Parse("""
            [{"title":"Organize imports","command":"gopls.organize","arguments":[1]},
             {"title":"Remove unused import","kind":"quickfix","isPreferred":true,"edit":{"changes":{}}},
             {"title":"Not now","kind":"refactor","disabled":{"reason":"no"}}]
            """);

        var actions = LspJson.ReadCodeActions(result);

        Assert.Equal(["Organize imports", "Remove unused import"], actions.Select(a => a.Title));
        Assert.Equal("gopls.organize", actions[0].Command!.Name);
        Assert.True(actions[1].IsPreferred);
        Assert.NotNull(actions[1].Edit);
    }
}
