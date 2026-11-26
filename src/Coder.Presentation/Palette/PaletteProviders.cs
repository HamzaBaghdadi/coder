using Coder.Core.Files;
using Coder.Core.Text;
using Coder.Presentation.Commands;
using Coder.Presentation.Editors;
using Coder.Presentation.Explorer;

namespace Coder.Presentation.Palette;

/// <summary>"&gt;" — run any registered command.</summary>
public sealed class CommandPaletteProvider(CommandRegistry commands) : IPaletteProvider
{
    public string Prefix => ">";

    public string Placeholder => "Type a command";

    public IEnumerable<PaletteItem> GetItems(string query) =>
        CommandPaletteViewModel.Rank(
            commands.All
                .OrderBy(command => command.FullTitle, StringComparer.OrdinalIgnoreCase)
                .Select(command => new PaletteItem(command.FullTitle, string.Empty, command.KeyBinding, command.Execute)),
            query);
}

/// <summary>No prefix — open a file from the workspace by fuzzy name.</summary>
public sealed class FilePaletteProvider(IFileSystem fileSystem, ExplorerViewModel explorer, Func<string, Task> openFile)
    : IPaletteProvider
{
    private const int MaxIndexedFiles = 50_000;

    private string? _indexedRoot;
    private List<string> _files = [];

    public string Prefix => string.Empty;

    public string Placeholder => explorer.HasFolder
        ? "Search files by name (append : to go to a line, > for commands)"
        : "Open a folder to search its files, or type > for commands";

    public IEnumerable<PaletteItem> GetItems(string query)
    {
        if (explorer.RootPath is not { } root)
        {
            return [];
        }

        if (_indexedRoot != root)
        {
            // Indexed once per folder on first use; later queries only filter in memory.
            _files = fileSystem.EnumerateFiles(root).Take(MaxIndexedFiles).ToList();
            _indexedRoot = root;
        }

        var items = _files.Select(path => new PaletteItem(
            Path.GetFileName(path),
            Path.GetRelativePath(root, Path.GetDirectoryName(path) ?? root),
            null,
            () => openFile(path)) { FilePath = path });
        return CommandPaletteViewModel.Rank(items, query);
    }
}

/// <summary>":" — jump to a line in the active editor.</summary>
public sealed class GoToLinePaletteProvider(EditorGroupViewModel editors) : IPaletteProvider
{
    public string Prefix => ":";

    public string Placeholder => editors.ActiveEditor is { } tab
        ? $"Type a line number between 1 and {tab.Document.LineCount}"
        : "Open a file to go to a line";

    public IEnumerable<PaletteItem> GetItems(string query)
    {
        if (editors.ActiveEditor is not { } tab)
        {
            yield break;
        }

        if (!int.TryParse(query, out var lineNumber))
        {
            var current = tab.Editor.CaretPosition;
            yield return new PaletteItem($"Current line: {current.Line + 1}", $"Type a number between 1 and {tab.Document.LineCount}", null, () => Task.CompletedTask);
            yield break;
        }

        var line = Math.Clamp(lineNumber, 1, tab.Document.LineCount) - 1;
        yield return new PaletteItem($"Go to line {line + 1}", tab.Title, null, () =>
        {
            tab.Editor.SetCaret(tab.Document.GetOffset(new TextPosition(line, 0)));
            return Task.CompletedTask;
        });
    }
}
