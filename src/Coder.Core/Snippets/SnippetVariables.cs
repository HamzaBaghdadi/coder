using System.Globalization;

namespace Coder.Core.Snippets;

/// <summary>Where a snippet is being inserted, for its variables.</summary>
/// <param name="FilePath">The file, or null when it is unsaved.</param>
/// <param name="WorkspaceFolder">The open folder, or null.</param>
/// <param name="LineText">The caret's line.</param>
/// <param name="LineIndex">The caret's line, zero-based.</param>
/// <param name="Word">The word at the caret (what was typed of the prefix).</param>
public sealed record SnippetContext(
    string? FilePath,
    string? WorkspaceFolder,
    string LineText,
    int LineIndex,
    string Word,
    string SelectedText,
    string? LineComment,
    (string Open, string Close)? BlockComment,
    DateTime Now);

/// <summary>
/// VS Code's snippet variables (<c>TM_FILENAME_BASE</c>, <c>CURRENT_YEAR</c>, <c>UUID</c>...), resolved for
/// a <see cref="SnippetContext"/>; pass <see cref="Resolve"/> to <see cref="Lsp.Snippet.Expand"/>.
/// </summary>
public static class SnippetVariables
{
    /// <summary>The variable's value, or null for a name VS Code doesn't define.</summary>
    public static string? Resolve(string name, SnippetContext context)
    {
        var now = context.Now;
        var culture = CultureInfo.InvariantCulture;
        return name switch
        {
            "TM_FILENAME" => context.FilePath is null ? "Untitled" : Path.GetFileName(context.FilePath),
            "TM_FILENAME_BASE" => context.FilePath is null ? "Untitled" : Path.GetFileNameWithoutExtension(context.FilePath),
            "TM_DIRECTORY" => context.FilePath is null ? string.Empty : Path.GetDirectoryName(context.FilePath) ?? string.Empty,
            "TM_FILEPATH" => context.FilePath ?? string.Empty,
            "RELATIVE_FILEPATH" => context.FilePath is null ? string.Empty
                : context.WorkspaceFolder is null ? context.FilePath : Path.GetRelativePath(context.WorkspaceFolder, context.FilePath),
            "TM_DIRECTORY_BASE" => context.FilePath is null ? string.Empty : Path.GetFileName(Path.GetDirectoryName(context.FilePath)) ?? string.Empty,
            "WORKSPACE_NAME" => context.WorkspaceFolder is null ? string.Empty : Path.GetFileName(context.WorkspaceFolder.TrimEnd('\\', '/')),
            "WORKSPACE_FOLDER" => context.WorkspaceFolder ?? string.Empty,
            "TM_SELECTED_TEXT" => context.SelectedText,
            "TM_CURRENT_LINE" => context.LineText,
            "TM_CURRENT_WORD" => context.Word,
            "TM_LINE_INDEX" => context.LineIndex.ToString(culture),
            "TM_LINE_NUMBER" => (context.LineIndex + 1).ToString(culture),
            "CLIPBOARD" => string.Empty, // not read: a snippet shouldn't paste what the user copied without asking
            "CURRENT_YEAR" => now.ToString("yyyy", culture),
            "CURRENT_YEAR_SHORT" => now.ToString("yy", culture),
            "CURRENT_MONTH" => now.ToString("MM", culture),
            "CURRENT_MONTH_NAME" => now.ToString("MMMM", culture),
            "CURRENT_MONTH_NAME_SHORT" => now.ToString("MMM", culture),
            "CURRENT_DATE" => now.ToString("dd", culture),
            "CURRENT_DAY_NAME" => now.ToString("dddd", culture),
            "CURRENT_DAY_NAME_SHORT" => now.ToString("ddd", culture),
            "CURRENT_HOUR" => now.ToString("HH", culture),
            "CURRENT_MINUTE" => now.ToString("mm", culture),
            "CURRENT_SECOND" => now.ToString("ss", culture),
            "CURRENT_SECONDS_UNIX" => new DateTimeOffset(now).ToUnixTimeSeconds().ToString(culture),
            "CURRENT_TIMEZONE_OFFSET" => new DateTimeOffset(now).ToString("zzz", culture),
            "RANDOM" => Random.Shared.Next(0, 1_000_000).ToString("D6", culture),
            "RANDOM_HEX" => Random.Shared.Next(0, 0x1000000).ToString("x6", culture),
            "UUID" => Guid.NewGuid().ToString(),
            "LINE_COMMENT" => context.LineComment ?? context.BlockComment?.Open ?? string.Empty,
            "BLOCK_COMMENT_START" => context.BlockComment?.Open ?? context.LineComment ?? string.Empty,
            "BLOCK_COMMENT_END" => context.BlockComment?.Close ?? string.Empty,
            _ => null,
        };
    }
}
