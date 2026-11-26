using System.Text;
using Coder.Core.Files;
using Coder.Core.Search;

namespace Coder.Core.Tests;

public sealed class TextReplacerTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("coder-replace-").FullName;

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public void Replaces_every_plain_match()
    {
        var (text, count) = TextReplacer.ReplaceAll("a.b a.b", "a.b", "x", new SearchOptions());

        Assert.Equal("x x", text);
        Assert.Equal(2, count);
    }

    [Fact]
    public void Regex_replacement_expands_groups_without_a_match_limit()
    {
        var input = string.Concat(Enumerable.Repeat("k1=v1;", TextSearch.MaxMatches + 5));

        var (text, count) = TextReplacer.ReplaceAll(input, @"(\w+)=(\w+)", "$2=$1", new SearchOptions(UseRegex: true));

        Assert.Equal(TextSearch.MaxMatches + 5, count);
        Assert.StartsWith("v1=k1;v1=k1;", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task File_replace_keeps_crlf_and_utf8_bom()
    {
        var path = Path.Combine(_directory, "a.txt");
        var withBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        await File.WriteAllTextAsync(path, "old one\r\nold two\r\n", withBom, Cancellation);

        var count = await new FileReplacer(new LocalFileSystem()).ReplaceAsync(path, "old", "new", new SearchOptions(), Cancellation);

        var bytes = await File.ReadAllBytesAsync(path, Cancellation);
        Assert.Equal(2, count);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal("new one\r\nnew two\r\n", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
    }

    [Fact]
    public async Task File_without_matches_is_not_rewritten()
    {
        var path = Path.Combine(_directory, "b.txt");
        await File.WriteAllTextAsync(path, "nothing here", Cancellation);
        var before = File.GetLastWriteTimeUtc(path);

        var count = await new FileReplacer(new LocalFileSystem()).ReplaceAsync(path, "zzz", "y", new SearchOptions(), Cancellation);

        Assert.Equal(0, count);
        Assert.Equal(before, File.GetLastWriteTimeUtc(path));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
