using System.Globalization;
using Coder.Core.Lsp;

namespace Coder.Core.Git;

/// <summary>What happened to a file, in one area (the index or the working tree).</summary>
public enum GitChangeKind
{
    Modified,
    Added,
    Deleted,
    Renamed,
    Copied,
    TypeChanged,
    Untracked,
    Conflicted,
}

/// <summary>A changed file. <see cref="OriginalPath"/> is set for renames and copies.</summary>
public sealed record GitChange(string Path, string RelativePath, GitChangeKind Kind, string? OriginalPath = null)
{
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>The one-letter code shown next to the file (M, A, D, R, U, C, T, !).</summary>
    public string Letter => Kind switch
    {
        GitChangeKind.Modified => "M",
        GitChangeKind.Added => "A",
        GitChangeKind.Deleted => "D",
        GitChangeKind.Renamed => "R",
        GitChangeKind.Copied => "C",
        GitChangeKind.TypeChanged => "T",
        GitChangeKind.Untracked => "U",
        _ => "!",
    };
}

/// <summary>
/// A snapshot of <c>git status</c>: the branch, its distance from upstream, and the changes split
/// the way Source Control shows them (merge conflicts, staged, and working tree changes).
/// </summary>
public sealed class GitStatus
{
    private readonly Dictionary<string, GitChangeKind> _byPath = new(DocumentUri.PathComparer);
    private readonly HashSet<string> _changedFolders = new(DocumentUri.PathComparer);

    public GitStatus(string root, string? branch, string? upstream, int ahead, int behind,
        IReadOnlyList<GitChange> conflicts, IReadOnlyList<GitChange> staged, IReadOnlyList<GitChange> unstaged, bool isUpstreamGone = false)
    {
        IsUpstreamGone = isUpstreamGone;
        Root = root;
        Branch = branch;
        Upstream = upstream;
        Ahead = ahead;
        Behind = behind;
        Conflicts = conflicts;
        Staged = staged;
        Unstaged = unstaged;

        // Working tree state wins for decorations (it is what the file on disk looks like), then conflicts.
        foreach (var change in staged.Concat(unstaged).Concat(conflicts))
        {
            _byPath[change.Path] = change.Kind;
            for (var folder = System.IO.Path.GetDirectoryName(change.Path); folder is not null && folder.Length > root.Length; folder = System.IO.Path.GetDirectoryName(folder))
            {
                if (!_changedFolders.Add(folder))
                {
                    break; // its parents were added with it
                }
            }
        }
    }

    public string Root { get; }

    /// <summary>The checked-out branch, or null when HEAD is detached.</summary>
    public string? Branch { get; }

    public string? Upstream { get; }

    /// <summary>
    /// The branch is set to track an upstream that does not exist (a clone of an empty repository, or a
    /// remote branch that was deleted): it needs publishing again.
    /// </summary>
    public bool IsUpstreamGone { get; }

    public int Ahead { get; }

    public int Behind { get; }

    public IReadOnlyList<GitChange> Conflicts { get; }

    public IReadOnlyList<GitChange> Staged { get; }

    public IReadOnlyList<GitChange> Unstaged { get; }

    public int ChangeCount => Conflicts.Count + Staged.Count + Unstaged.Count;

    /// <summary>How the file at <paramref name="path"/> differs, or null when it is unchanged.</summary>
    public GitChangeKind? KindOf(string path) => _byPath.TryGetValue(path, out var kind) ? kind : null;

    /// <summary>True when a folder contains changed files (shown as a dot in the explorer).</summary>
    public bool ContainsChanges(string folder) => _changedFolders.Contains(System.IO.Path.TrimEndingDirectorySeparator(folder));

    /// <summary>
    /// Parses <c>git status --porcelain=v2 -z --branch</c>. Records are NUL-separated; a rename record is
    /// followed by one more record holding the original path.
    /// </summary>
    public static GitStatus Parse(string output, string root)
    {
        string? branch = null, upstream = null;
        int ahead = 0, behind = 0;
        var hasAheadBehind = false;
        var conflicts = new List<GitChange>();
        var staged = new List<GitChange>();
        var unstaged = new List<GitChange>();

        var records = output.Split('\0');
        for (var i = 0; i < records.Length; i++)
        {
            var record = records[i];
            if (record.Length < 2)
            {
                continue;
            }

            switch (record[0])
            {
                case '#':
                    hasAheadBehind |= record.StartsWith("# branch.ab ", StringComparison.Ordinal);
                    ParseHeader(record, ref branch, ref upstream, ref ahead, ref behind);
                    break;
                case '?':
                    unstaged.Add(Change(root, record[2..], GitChangeKind.Untracked));
                    break;
                case '1':
                    AddOrdinary(root, record, Fields(record, 9)[8], null, staged, unstaged);
                    break;
                case '2':
                    var original = i + 1 < records.Length ? records[++i] : null;
                    AddOrdinary(root, record, Fields(record, 10)[9], original, staged, unstaged);
                    break;
                case 'u':
                    conflicts.Add(Change(root, Fields(record, 11)[10], GitChangeKind.Conflicted));
                    break;
            }
        }

        // git prints no ahead/behind line when the upstream branch is missing.
        return new GitStatus(root, branch, upstream, ahead, behind, conflicts, staged, unstaged, isUpstreamGone: upstream is not null && !hasAheadBehind);
    }

    private static void ParseHeader(string record, ref string? branch, ref string? upstream, ref int ahead, ref int behind)
    {
        var parts = record.Split(' ');
        switch (parts[1])
        {
            case "branch.head":
                branch = parts[2] == "(detached)" ? null : record["# branch.head ".Length..];
                break;
            case "branch.upstream":
                upstream = record["# branch.upstream ".Length..];
                break;
            case "branch.ab" when parts.Length >= 4:
                ahead = int.Parse(parts[2].TrimStart('+'), CultureInfo.InvariantCulture);
                behind = int.Parse(parts[3].TrimStart('-'), CultureInfo.InvariantCulture);
                break;
        }
    }

    /// <summary>"1 XY …" / "2 XY …": X is the index (staged) state, Y the working tree state.</summary>
    private static void AddOrdinary(string root, string record, string path, string? originalPath, List<GitChange> staged, List<GitChange> unstaged)
    {
        var x = record[2];
        var y = record[3];
        if (x != '.')
        {
            staged.Add(Change(root, path, KindOf(x), originalPath is null ? null : FullPath(root, originalPath)));
        }

        if (y != '.')
        {
            unstaged.Add(Change(root, path, KindOf(y)));
        }
    }

    private static GitChangeKind KindOf(char code) => code switch
    {
        'A' => GitChangeKind.Added,
        'D' => GitChangeKind.Deleted,
        'R' => GitChangeKind.Renamed,
        'C' => GitChangeKind.Copied,
        'T' => GitChangeKind.TypeChanged,
        _ => GitChangeKind.Modified,
    };

    /// <summary>Splits off the fixed fields; the last one is the path, which may contain spaces.</summary>
    private static string[] Fields(string record, int count) => record.Split(' ', count);

    private static GitChange Change(string root, string relativePath, GitChangeKind kind, string? originalPath = null) =>
        new(FullPath(root, relativePath), relativePath, kind, originalPath);

    private static string FullPath(string root, string relativePath) =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relativePath));
}

/// <summary>A local or remote branch. <see cref="Upstream"/> is the branch a local one tracks.</summary>
public sealed record GitBranch(string Name, bool IsRemote, bool IsCurrent, string? Upstream, string LastCommitSubject);

/// <summary>A stash: its reference ("stash@{0}"), its description and how long ago it was made.</summary>
public sealed record GitStash(string Reference, string Description, string Age);
