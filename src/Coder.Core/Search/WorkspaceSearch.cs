using Coder.Core.Files;
using Coder.Core.Text;

namespace Coder.Core.Search;

/// <summary>One match inside a file, with a single-line preview for the results list.</summary>
/// <param name="Position">Zero-based line and column of the match start.</param>
/// <param name="Preview">The trimmed line text shown in the results.</param>
/// <param name="PreviewMatchStart">Where the match begins inside <paramref name="Preview"/>.</param>
public sealed record LineMatch(TextPosition Position, int Length, string Preview, int PreviewMatchStart);

public sealed record FileSearchResult(string Path, IReadOnlyList<LineMatch> Matches);

/// <summary>
/// Searches every text file of a folder. Runs on a background thread, reports each file's matches as
/// soon as they are found, and stops promptly when cancelled. Large and binary files are skipped.
/// </summary>
public sealed class WorkspaceSearch(IFileSystem fileSystem)
{
    /// <summary>Files bigger than this are skipped; they are almost always generated or data files.</summary>
    public const long MaxFileSize = 4 * 1024 * 1024;

    /// <summary>Stops collecting after this many matches across all files.</summary>
    public const int MaxTotalMatches = 10_000;

    private const int PreviewContext = 60;

    /// <summary>Searches <paramref name="root"/> and returns the total number of matches found.</summary>
    public Task<int> SearchAsync(string root, string query, SearchOptions options, IProgress<FileSearchResult> progress, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            var total = 0;
            foreach (var path in fileSystem.EnumerateFiles(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (total >= MaxTotalMatches || !IsSearchable(path))
                {
                    continue;
                }

                var result = await SearchFileAsync(path, query, options, cancellationToken).ConfigureAwait(false);
                if (result is { Matches.Count: > 0 })
                {
                    total += result.Matches.Count;
                    progress.Report(result);
                }
            }

            return total;
        }, cancellationToken);

    private async Task<FileSearchResult?> SearchFileAsync(string path, string query, SearchOptions options, CancellationToken cancellationToken)
    {
        string text;
        try
        {
            text = LineEndings.Normalize((await fileSystem.ReadTextAsync(path, cancellationToken).ConfigureAwait(false)).Text);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null; // Binary, locked or vanished files are simply not results.
        }

        var search = TextSearch.FindAll(text, query, options);
        if (search.Matches.Count == 0)
        {
            return null;
        }

        return new FileSearchResult(path, ToLineMatches(text, search.Matches));
    }

    /// <summary>Converts offsets to line/column and cuts a short preview around each match.</summary>
    private static List<LineMatch> ToLineMatches(string text, IReadOnlyList<SearchMatch> matches)
    {
        var result = new List<LineMatch>(matches.Count);
        int line = 0, lineStart = 0, scanned = 0;
        foreach (var match in matches)
        {
            // Matches are sorted, so line numbers are found by scanning forward only once in total.
            for (var i = text.IndexOf('\n', scanned); i >= 0 && i < match.Start; i = text.IndexOf('\n', i + 1))
            {
                line++;
                lineStart = i + 1;
            }

            scanned = lineStart;
            var lineEnd = text.IndexOf('\n', match.Start);
            if (lineEnd < 0)
            {
                lineEnd = text.Length;
            }

            var column = match.Start - lineStart;
            var previewStart = Math.Max(lineStart, match.Start - PreviewContext);
            var previewEnd = Math.Min(lineEnd, match.End + PreviewContext);
            var raw = text[previewStart..previewEnd];
            var trimmed = raw.TrimStart();
            var visibleLength = Math.Min(match.Length, Math.Max(0, previewEnd - match.Start));
            var matchInPreview = Math.Max(0, match.Start - previewStart - (raw.Length - trimmed.Length));
            result.Add(new LineMatch(new TextPosition(line, column), visibleLength, trimmed.TrimEnd(), matchInPreview));
        }

        return result;
    }

    private static bool IsSearchable(string path)
    {
        try
        {
            return new FileInfo(path).Length <= MaxFileSize;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
