using Coder.Core.Formatting;
using Coder.Core.Lsp;
using Coder.Core.Text;
using Coder.Presentation.Editors;
using Coder.Presentation.Palette;

namespace Coder.Presentation.LanguageFeatures;

/// <summary>
/// The language commands that need the palette or several files: Rename Symbol (F2),
/// Quick Fix (Ctrl+.) and Format Document (Shift+Alt+F, Prettier for web files). Results and
/// questions use the command palette as a quick pick, like VS Code's input boxes.
/// </summary>
public sealed class LanguageCommands(
    LanguageServices languages,
    EditorGroupViewModel editors,
    CommandPaletteViewModel palette,
    WorkspaceEditApplier edits,
    Action<string> notify,
    PrettierFormatter? prettier = null)
{
    /// <summary>F2: asks for the new name (pre-filled with the current one), then renames everywhere.</summary>
    public void Rename()
    {
        if (editors.ActiveEditor is not { } tab || !CheckSupport(tab, c => c.HasRename, "renaming"))
        {
            return;
        }

        // The symbol under the caret, or the one just before it ("helper|()"), like VS Code.
        var offset = tab.Editor.Selection.Active;
        var document = tab.Document;
        if ((offset >= document.Length || !IsIdentifierChar(document[offset])) && offset > 0 && IsIdentifierChar(document[offset - 1]))
        {
            offset--;
        }

        var (start, end) = tab.Editor.WordRangeAt(offset);
        var current = document.GetText(start, end - start).Trim();
        palette.Pick(new PickProvider("New name, then Enter to rename", query =>
        {
            var name = query.Trim();
            return name.Length == 0 || name == current
                ? [new PaletteItem($"Type a new name for \"{current}\"", "Renames it in every file that uses it", null, () => Task.CompletedTask)]
                : [new PaletteItem($"Rename \"{current}\" to \"{name}\"", "In every file that uses it", null, () => RenameAsync(tab, offset, name))];
        }), current);
    }

    public async Task RenameAsync(EditorTabViewModel tab, int offset, string newName)
    {
        var edit = await languages.RenameAsync(tab, offset, newName, CancellationToken.None);
        if (edit is null || edit.EditCount == 0)
        {
            notify("Nothing to rename here.");
            return;
        }

        var files = await ApplyAsync(edit);
        notify($"Renamed to \"{newName}\": {edit.EditCount} {Plural(edit.EditCount, "change")} in {files} {Plural(files, "file")}.");
    }

    /// <summary>Ctrl+.: the fixes and refactorings for the caret or selection, preferred ones first.</summary>
    public async Task QuickFixAsync()
    {
        if (editors.ActiveEditor is not { } tab || !CheckSupport(tab, c => c.HasCodeActions, "quick fixes"))
        {
            return;
        }

        var selection = tab.Editor.Selection;
        var actions = await languages.CodeActionsAsync(tab, selection.Start, selection.End, CancellationToken.None);
        if (actions is not { Count: > 0 })
        {
            notify("No quick fixes available here.");
            return;
        }

        var items = actions
            .OrderByDescending(a => a.IsPreferred)
            .ThenBy(a => a.Kind?.StartsWith("quickfix", StringComparison.Ordinal) == true ? 0 : 1)
            .Select(action => new PaletteItem(action.Title, KindLabel(action.Kind), action.IsPreferred ? "preferred" : null, () => RunAsync(tab, action)))
            .ToList();
        palette.Pick(PickProvider.FromList("Choose a fix or refactoring", items));
    }

    /// <summary>Shift+Alt+F: formats the whole document with the language server, as one undo step.</summary>
    public async Task FormatDocumentAsync()
    {
        // Web files (HTML, CSS, JS/TS, JSON, Markdown, YAML…) format with Prettier, following the project's settings.
        if (editors.ActiveEditor is { FilePath: { } path } webTab && PrettierFormatter.Handles(path) && prettier is { IsAvailable: true })
        {
            notify("Formatting with Prettier…");
            var (formatted, error) = await prettier.FormatAsync(path, webTab.Document.GetTextForSave());
            if (formatted is null)
            {
                notify($"Prettier: {error?.Split('\n')[0].Trim()}");
                return;
            }

            var before = webTab.Document.Version;
            webTab.Editor.ReplaceText(LineEndings.Normalize(formatted));
            notify(webTab.Document.Version == before ? "Already formatted." : "Formatted with Prettier.");
            return;
        }

        if (editors.ActiveEditor is not { } tab || !CheckSupport(tab, c => c.HasFormatting, "formatting"))
        {
            return;
        }

        var changes = await languages.FormatAsync(tab, CancellationToken.None);
        if (changes is null)
        {
            return;
        }

        var document = tab.Document;
        tab.Editor.ApplyEdits(changes.Select(c => (document.GetOffset(c.Range.Start), document.GetOffset(c.Range.End), LineEndings.Normalize(c.NewText))).ToList());
        notify(changes.Count == 0 ? "Already formatted." : "Formatted.");
    }

    /// <summary>Applies an action's edit, then runs its command (whose edits arrive separately).</summary>
    private async Task RunAsync(EditorTabViewModel tab, CodeAction action)
    {
        if (action.Edit is { } edit)
        {
            await ApplyAsync(edit);
        }

        if (action.Command is { } command)
        {
            await languages.ExecuteCommandAsync(tab, command);
        }
    }

    private async Task<int> ApplyAsync(WorkspaceEdit edit)
    {
        try
        {
            return await edits.ApplyAsync(edit);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            notify($"Some files could not be changed: {exception.Message}");
            return 0;
        }
    }

    private bool CheckSupport(EditorTabViewModel tab, Func<ServerCapabilities, bool> feature, string what)
    {
        if (languages.Supports(tab, feature))
        {
            return true;
        }

        notify(languages.StatusText.Length == 0
            ? $"No language server for {tab.Language.DisplayName} files, so {what} is not available."
            : $"The {tab.Language.DisplayName} language server does not support {what}.");
        return false;
    }

    private static string KindLabel(string? kind) => kind switch
    {
        null or "" => string.Empty,
        _ when kind.StartsWith("quickfix", StringComparison.Ordinal) => "Quick fix",
        _ when kind.StartsWith("refactor", StringComparison.Ordinal) => "Refactor",
        _ when kind.StartsWith("source", StringComparison.Ordinal) => "Source action",
        _ => kind,
    };

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static string Plural(int count, string word) => count == 1 ? word : word + "s";
}
