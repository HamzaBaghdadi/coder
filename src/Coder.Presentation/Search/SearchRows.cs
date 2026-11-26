using Coder.Core.Search;
using Coder.Core.Syntax;
using Coder.Presentation.Mvvm;

namespace Coder.Presentation.Search;

/// <summary>A row in the flat, virtualized search results list: a file header or one of its matches.</summary>
public abstract class SearchRowViewModel : ObservableObject;

public sealed class SearchFileRowViewModel(FileSearchResult result, string root) : SearchRowViewModel
{
    private bool _isExpanded = true;

    public FileSearchResult Result { get; } = result;

    public string FileName => Path.GetFileName(Result.Path);

    /// <summary>Folder relative to the workspace, shown dimmed after the file name.</summary>
    public string Folder { get; } = RelativeFolder(result.Path, root);

    public int MatchCount => Result.Matches.Count;

    public string IconColor => Languages.FromFileName(Result.Path).IconColor;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    internal List<SearchMatchRowViewModel> MatchRows { get; } = [];

    private static string RelativeFolder(string path, string root)
    {
        var relative = Path.GetRelativePath(root, Path.GetDirectoryName(path) ?? root);
        return relative == "." ? string.Empty : relative;
    }
}

public sealed class SearchMatchRowViewModel(SearchFileRowViewModel file, LineMatch match) : SearchRowViewModel
{
    public SearchFileRowViewModel File { get; } = file;

    public LineMatch Match { get; } = match;

    public string Before => Match.Preview[..Math.Min(Match.PreviewMatchStart, Match.Preview.Length)];

    public string Matched => Match.Preview.Substring(
        Math.Min(Match.PreviewMatchStart, Match.Preview.Length),
        Math.Min(Match.Length, Math.Max(0, Match.Preview.Length - Match.PreviewMatchStart)));

    public string After => Match.Preview[Math.Min(Match.PreviewMatchStart + Matched.Length, Match.Preview.Length)..];

    public string LineLabel => (Match.Position.Line + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
