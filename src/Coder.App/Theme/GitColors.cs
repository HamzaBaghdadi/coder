using Avalonia.Media;
using Coder.Core.Git;

namespace Coder.App.Theme;

/// <summary>The color of each kind of git change, shared by the explorer and Source Control.</summary>
public static class GitColors
{
    /// <summary>The text color for a file name; unchanged files keep the normal text color.</summary>
    public static IBrush For(GitChangeKind? kind) => kind switch
    {
        null => Palette.TextPrimary,
        GitChangeKind.Added or GitChangeKind.Untracked or GitChangeKind.Renamed or GitChangeKind.Copied => Palette.GitAdded,
        GitChangeKind.Deleted => Palette.GitDeleted,
        GitChangeKind.Conflicted => Palette.GitConflict,
        _ => Palette.GitModified,
    };
}
