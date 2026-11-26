using System.Globalization;

namespace Coder.Core.Git;

/// <summary>One commit of the history.</summary>
/// <param name="Refs">Branch and tag names pointing at it, as git decorates them ("HEAD -> main", "origin/main", "tag: v1").</param>
public sealed record GitCommit(
    string Hash,
    IReadOnlyList<string> Parents,
    string Author,
    string Email,
    DateTimeOffset Date,
    string Subject,
    string Body,
    IReadOnlyList<string> Refs)
{
    public string ShortHash => Hash.Length > 7 ? Hash[..7] : Hash;

    public bool IsMerge => Parents.Count > 1;

    /// <summary>The first parent's hash, or null for the first commit.</summary>
    public string? Parent => Parents.Count > 0 ? Parents[0] : null;
}

/// <summary>Who last changed one line, from <c>git blame</c>.</summary>
/// <param name="IsCommitted">False for lines changed since the last commit (in the working tree or the editor).</param>
public sealed record GitBlameLine(string Hash, string Author, string Email, DateTimeOffset Date, string Summary, bool IsCommitted)
{
    public string ShortHash => Hash.Length > 7 ? Hash[..7] : Hash;
}

/// <summary>Parses the output of the history commands (kept apart from running them, so it is easy to test).</summary>
public static class GitHistoryParser
{
    /// <summary>
    /// The <c>git log</c> format this parser reads: fields separated by U+001F, records by NUL (<c>-z</c>).
    /// </summary>
    public const string LogFormat = "--format=%H%x1f%P%x1f%an%x1f%ae%x1f%at%x1f%D%x1f%s%x1f%b";

    private const string Uncommitted = "0000000000000000000000000000000000000000";

    public static IReadOnlyList<GitCommit> ParseLog(string output)
    {
        var commits = new List<GitCommit>();
        foreach (var record in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = record.TrimStart('\n').Split('\u001f');
            if (fields.Length < 8)
            {
                continue;
            }

            commits.Add(new GitCommit(
                fields[0],
                fields[1].Split(' ', StringSplitOptions.RemoveEmptyEntries),
                fields[2],
                fields[3],
                DateTimeOffset.FromUnixTimeSeconds(long.Parse(fields[4], CultureInfo.InvariantCulture)),
                fields[6],
                fields[7].Trim(),
                fields[5].Split(", ", StringSplitOptions.RemoveEmptyEntries)));
        }

        return commits;
    }

    /// <summary>
    /// Parses <c>git diff --name-status -z -M</c>: a status (with a score for renames and copies), then
    /// the path, or the original and the new path.
    /// </summary>
    public static IReadOnlyList<GitChange> ParseNameStatus(string output, string root)
    {
        var changes = new List<GitChange>();
        var fields = output.Split('\0');
        for (var i = 0; i + 1 < fields.Length; i++)
        {
            var status = fields[i];
            if (status.Length == 0)
            {
                continue;
            }

            var kind = status[0] switch
            {
                'A' => GitChangeKind.Added,
                'D' => GitChangeKind.Deleted,
                'R' => GitChangeKind.Renamed,
                'C' => GitChangeKind.Copied,
                'T' => GitChangeKind.TypeChanged,
                'U' => GitChangeKind.Conflicted,
                _ => GitChangeKind.Modified,
            };

            string? original = null;
            if (kind is GitChangeKind.Renamed or GitChangeKind.Copied && i + 2 < fields.Length)
            {
                original = Full(root, fields[++i]);
            }

            var relative = fields[++i];
            changes.Add(new GitChange(Full(root, relative), relative, kind, original));
        }

        return changes;
    }

    /// <summary>
    /// Parses <c>git blame --porcelain</c>: each line starts with "hash original final [count]"; the first
    /// time a commit appears, header lines (author, author-mail, author-time, summary…) follow; then the
    /// line's text after a tab.
    /// </summary>
    public static IReadOnlyList<GitBlameLine> ParseBlame(string output)
    {
        var commits = new Dictionary<string, GitBlameLine>(StringComparer.Ordinal);
        var lines = new List<GitBlameLine>();
        string? hash = null;
        string author = string.Empty, email = string.Empty, summary = string.Empty;
        long time = 0;
        foreach (var line in output.Split('\n'))
        {
            if (line.StartsWith('\t'))
            {
                if (hash is null)
                {
                    continue;
                }

                if (!commits.TryGetValue(hash, out var info))
                {
                    var committed = hash != Uncommitted;
                    info = new GitBlameLine(hash, author, email, DateTimeOffset.FromUnixTimeSeconds(time), summary, committed);
                    commits[hash] = info;
                }

                lines.Add(info);
                hash = null;
                continue;
            }

            var space = line.IndexOf(' ', StringComparison.Ordinal);
            var key = space < 0 ? line : line[..space];
            var value = space < 0 ? string.Empty : line[(space + 1)..];
            if (key.Length == 40 && IsHex(key))
            {
                hash = key;
                if (!commits.ContainsKey(hash))
                {
                    (author, email, summary, time) = (string.Empty, string.Empty, string.Empty, 0);
                }

                continue;
            }

            switch (key)
            {
                case "author":
                    author = value;
                    break;
                case "author-mail":
                    email = value.Trim('<', '>');
                    break;
                case "author-time":
                    time = long.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "summary":
                    summary = value;
                    break;
            }
        }

        return lines;
    }

    private static bool IsHex(string text)
    {
        foreach (var c in text)
        {
            if (!char.IsAsciiHexDigitLower(c) && !char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static string Full(string root, string relative) => Path.GetFullPath(Path.Combine(root, relative));
}
