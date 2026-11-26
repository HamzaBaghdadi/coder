using Coder.Core.Search;
using Coder.Presentation.Editors;

namespace Coder.Presentation.Search;

/// <summary>What a replace-in-files run did.</summary>
/// <param name="FailedFiles">Files that could not be read or written, with the reason.</param>
public sealed record ReplaceOutcome(int Replacements, int Files, IReadOnlyList<(string Path, string Reason)> FailedFiles);

/// <summary>
/// Applies a replacement across files. A file open in an editor is changed in its buffer (undoable,
/// left unsaved for review, like VS Code); any other file is rewritten on disk.
/// </summary>
public sealed class ReplaceInFiles(EditorGroupViewModel editors, FileReplacer fileReplacer)
{
    public async Task<ReplaceOutcome> ReplaceAsync(IEnumerable<string> paths, string query, string replacement, SearchOptions options)
    {
        int replacements = 0, files = 0;
        var failures = new List<(string, string)>();
        foreach (var path in paths)
        {
            try
            {
                var count = FindOpenTab(path) is { } tab
                    ? ReplaceInEditor(tab, query, replacement, options)
                    : await fileReplacer.ReplaceAsync(path, query, replacement, options);
                if (count > 0)
                {
                    replacements += count;
                    files++;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failures.Add((path, exception.Message));
            }
        }

        return new ReplaceOutcome(replacements, files, failures);
    }

    private static int ReplaceInEditor(EditorTabViewModel tab, string query, string replacement, SearchOptions options)
    {
        var text = tab.Document.GetText();
        var matches = TextSearch.FindAll(text, query, options, maxMatches: int.MaxValue).Matches;
        tab.Editor.ReplaceMatches(matches, TextSearch.CreateReplacer(text, query, replacement, options));
        return matches.Count;
    }

    private EditorTabViewModel? FindOpenTab(string path) => editors.FindTab(path);
}
