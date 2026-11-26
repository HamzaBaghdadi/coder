using Coder.Core.Files;
using Coder.Core.Search;

namespace Coder.Core.Tests;

public sealed class WorkspaceSearchTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("coder-search-").FullName;

    [Fact]
    public async Task Finds_matches_with_line_positions_and_skips_ignored_folders()
    {
        Write("a.txt", "first line\r\nsecond needle here\nthird");
        Write("node_modules/lib.js", "needle");
        Write("sub/b.cs", "var needle = needle;");

        var results = new List<FileSearchResult>();
        var total = await new WorkspaceSearch(new LocalFileSystem())
            .SearchAsync(_root, "needle", new SearchOptions(), new SynchronousProgress(results.Add), CancellationToken.None);

        Assert.Equal(3, total);
        var a = Assert.Single(results, r => r.Path.EndsWith("a.txt", StringComparison.Ordinal));
        var match = Assert.Single(a.Matches);
        Assert.Equal(new Text.TextPosition(1, 7), match.Position);
        Assert.Equal("needle", match.Preview.Substring(match.PreviewMatchStart, match.Length));
        Assert.DoesNotContain(results, r => r.Path.Contains("node_modules", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Binary_files_are_skipped()
    {
        File.WriteAllBytes(Path.Combine(_root, "image.bin"), [0x6E, 0x65, 0x00, 0x65, 0x64]);
        Write("text.txt", "ne");

        var results = new List<FileSearchResult>();
        await new WorkspaceSearch(new LocalFileSystem())
            .SearchAsync(_root, "ne", new SearchOptions(), new SynchronousProgress(results.Add), CancellationToken.None);

        Assert.Single(results);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>Reports inline, unlike <see cref="Progress{T}"/>, so assertions see every result.</summary>
    private sealed class SynchronousProgress(Action<FileSearchResult> report) : IProgress<FileSearchResult>
    {
        public void Report(FileSearchResult value) => report(value);
    }
}
