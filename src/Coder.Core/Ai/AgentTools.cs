using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Coder.Core.Files;
using Coder.Core.Search;
using Coder.Core.Text;

namespace Coder.Core.Ai;

/// <summary>A change to one file the agent wants to make, waiting for the user's decision. Texts use '\n' line breaks.</summary>
/// <param name="Original">The file's text now; null for a new file.</param>
public sealed record EditProposal(string Path, string? Original, string Proposed);

/// <summary>The user's answer to an <see cref="EditProposal"/>, with optional words for the agent ("use tabs").</summary>
public sealed record EditDecision(bool Accepted, string? Feedback = null);

/// <summary>A command the agent wants to run, waiting for the user's decision.</summary>
public sealed record CommandProposal(string Command, string WorkingDirectory);

/// <summary>What a command printed (standard output and error, interleaved) and how it ended.</summary>
public sealed record CommandResult(int? ExitCode, string Output, bool TimedOut);

/// <summary>The workbench's side of the agent's tools: open documents, and the user's say over every change.</summary>
public interface IAgentToolHost
{
    /// <summary>The file's text in its editor (unsaved changes included), or null when it is not open.</summary>
    string? OpenDocumentText(string path);

    /// <summary>Shows the change as a diff and waits for the user to accept (and apply it) or reject it.</summary>
    Task<EditDecision> ReviewEditAsync(EditProposal proposal, CancellationToken cancellationToken);

    /// <summary>Asks the user whether the command may run; nothing runs without a yes.</summary>
    Task<bool> ConfirmCommandAsync(CommandProposal proposal, CancellationToken cancellationToken);
}

/// <summary>Runs a shell command line.</summary>
public interface ICommandRunner
{
    Task<CommandResult> RunAsync(string command, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// The agent's tools on the open folder: list, find, read and search files freely; edit and write files
/// only through the user's review; run commands only after the user confirms each one. Paths are
/// relative to the folder, and nothing outside it can be read or changed.
/// </summary>
public sealed class AgentToolbox(string root, IFileSystem fileSystem, IAgentToolHost host, ICommandRunner commands)
{
    public const int MaxReadLines = 2000;
    public const int MaxResultChars = 60_000;
    public const int MaxListed = 400;
    public const int MaxMatches = 150;
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(5);

    public string Root { get; } = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public static IReadOnlyList<ToolDefinition> Definitions { get; } =
    [
        new("list_directory", "List the files and folders in a folder of the workspace (folders end with /). Use \".\" for the workspace root.",
            """{"type":"object","properties":{"path":{"type":"string","description":"Folder path relative to the workspace root."}},"required":["path"]}"""),
        new("find_files", "Find files by name or glob pattern anywhere in the workspace (e.g. \"*.csproj\", \"src/**/*View.cs\", \"settings\"). Build output and .git are skipped.",
            """{"type":"object","properties":{"pattern":{"type":"string","description":"A glob (* ? **) or part of a file name."}},"required":["pattern"]}"""),
        new("read_file", "Read a text file of the workspace. Each line comes back prefixed with its number and a tab; the prefix is not part of the file. Read a range of lines of large files.",
            """{"type":"object","properties":{"path":{"type":"string","description":"File path relative to the workspace root."},"start_line":{"type":"integer","description":"First line to read (1-based)."},"end_line":{"type":"integer","description":"Last line to read (inclusive)."}},"required":["path"]}"""),
        new("search_text", "Search the text of every file in the workspace. Returns matching lines as path:line: text.",
            """{"type":"object","properties":{"query":{"type":"string","description":"The text or regular expression to find."},"is_regex":{"type":"boolean","description":"Treat the query as a .NET regular expression."},"match_case":{"type":"boolean"}},"required":["query"]}"""),
        new("edit_file", "Change part of an existing file by replacing exact text. old_text must match the file exactly (whitespace included) and once only, unless replace_all is true; include enough surrounding lines to make it unique. The user reviews the change as a diff before it is applied.",
            """{"type":"object","properties":{"path":{"type":"string"},"old_text":{"type":"string","description":"The exact text to replace."},"new_text":{"type":"string","description":"The text to put in its place."},"replace_all":{"type":"boolean","description":"Replace every occurrence."}},"required":["path","old_text","new_text"]}"""),
        new("write_file", "Create a new file, or replace a whole file's content. The user reviews the change as a diff before it is applied. Prefer edit_file for changes to part of a file.",
            """{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string","description":"The complete new content of the file."}},"required":["path","content"]}"""),
        new("run_command", "Run a shell command in the workspace folder (PowerShell on Windows, sh elsewhere) and get its output. The user must approve each command first. Use it to build, test, or run git; not for reading or editing files.",
            """{"type":"object","properties":{"command":{"type":"string","description":"The command line to run."}},"required":["command"]}"""),
    ];

    /// <summary>A short description of a call for the chat ("Read src/App.cs").</summary>
    public static string Describe(ToolCall call)
    {
        using var arguments = ProviderHttp.TryParse(call.Arguments);
        var args = arguments?.RootElement ?? default;
        string? Arg(string name) => args.ValueKind == JsonValueKind.Object ? ProviderHttp.Str(args, name) : null;
        return call.Name switch
        {
            "list_directory" => $"List {Arg("path") ?? "."}",
            "find_files" => $"Find files {Arg("pattern")}",
            "read_file" => $"Read {Arg("path")}",
            "search_text" => $"Search for \"{Arg("query")}\"",
            "edit_file" => $"Edit {Arg("path")}",
            "write_file" => $"Write {Arg("path")}",
            "run_command" => $"Run {Arg("command")}",
            _ => call.Name,
        };
    }

    /// <summary>Runs a call; the result (or what went wrong) is text for the model. Never throws for bad arguments.</summary>
    public async Task<ToolResult> InvokeAsync(ToolCall call, CancellationToken cancellationToken)
    {
        JsonDocument? document;
        try
        {
            document = JsonDocument.Parse(call.Arguments.Length == 0 ? "{}" : call.Arguments);
        }
        catch (JsonException)
        {
            return ToolResult.Error("The arguments were not valid JSON. Call the tool again with a JSON object.");
        }

        using (document)
        {
            var args = document.RootElement;
            if (args.ValueKind != JsonValueKind.Object)
            {
                return ToolResult.Error("The arguments must be a JSON object.");
            }

            try
            {
                return call.Name switch
                {
                    "list_directory" => List(args),
                    "find_files" => Find(args),
                    "read_file" => await ReadAsync(args, cancellationToken).ConfigureAwait(false),
                    "search_text" => await SearchAsync(args, cancellationToken).ConfigureAwait(false),
                    "edit_file" => await EditAsync(args, cancellationToken).ConfigureAwait(false),
                    "write_file" => await WriteAsync(args, cancellationToken).ConfigureAwait(false),
                    "run_command" => await RunAsync(args, cancellationToken).ConfigureAwait(false),
                    _ => ToolResult.Error($"There is no tool named {call.Name}."),
                };
            }
            catch (ToolArgumentException exception)
            {
                return ToolResult.Error(exception.Message);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return ToolResult.Error(exception.Message);
            }
        }
    }

    /// <summary>The full path for a workspace path; throws when it leaves the workspace.</summary>
    public string Resolve(string path)
    {
        var trimmed = path.Trim().Trim('"');
        var full = Path.GetFullPath(Path.IsPathRooted(trimmed) ? trimmed : Path.Combine(Root, trimmed.Length == 0 ? "." : trimmed));
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var inside = full.TrimEnd(Path.DirectorySeparatorChar).Equals(Root, comparison)
            || full.StartsWith(Root + Path.DirectorySeparatorChar, comparison);
        return inside ? full : throw new ToolArgumentException($"{path} is outside the workspace ({Root}); only files inside it can be used.");
    }

    public string Relative(string fullPath) => Path.GetRelativePath(Root, fullPath).Replace('\\', '/');

    private ToolResult List(JsonElement args)
    {
        var folder = Resolve(Required(args, "path", allowEmpty: true));
        if (!Directory.Exists(folder))
        {
            return ToolResult.Error($"{Relative(folder)} is not a folder.");
        }

        var entries = fileSystem.GetChildren(folder);
        var text = new StringBuilder();
        foreach (var entry in entries.Take(MaxListed))
        {
            text.Append(entry.Name).Append(entry.IsDirectory ? "/" : string.Empty).Append('\n');
        }

        if (entries.Count > MaxListed)
        {
            text.Append($"… and {entries.Count - MaxListed} more\n");
        }

        return ToolResult.Ok(entries.Count == 0 ? "(empty folder)" : text.ToString());
    }

    private ToolResult Find(JsonElement args)
    {
        var pattern = Required(args, "pattern").Replace('\\', '/');
        var glob = pattern.IndexOfAny(['*', '?']) >= 0;
        var regex = glob ? GlobToRegex(pattern) : null;
        var found = new List<string>();
        foreach (var file in fileSystem.EnumerateFiles(Root))
        {
            var relative = Relative(file);
            var matches = regex is not null
                ? regex.IsMatch(relative) || (!pattern.Contains('/', StringComparison.Ordinal) && regex.IsMatch(Path.GetFileName(file)))
                : relative.Contains(pattern, StringComparison.OrdinalIgnoreCase);
            if (matches)
            {
                found.Add(relative);
                if (found.Count >= MaxListed)
                {
                    break;
                }
            }
        }

        found.Sort(StringComparer.OrdinalIgnoreCase);
        return ToolResult.Ok(found.Count == 0 ? $"No files match {pattern}." : string.Join('\n', found) + (found.Count >= MaxListed ? "\n… (more not shown)" : string.Empty));
    }

    internal static Regex GlobToRegex(string glob)
    {
        var pattern = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                pattern.Append(".*");
                i++;
                if (i + 1 < glob.Length && glob[i + 1] == '/')
                {
                    pattern.Append("/?");
                    i++;
                }
            }
            else
            {
                pattern.Append(c switch
                {
                    '*' => "[^/]*",
                    '?' => "[^/]",
                    _ => Regex.Escape(c.ToString()),
                });
            }
        }

        return new Regex(pattern.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    private async Task<ToolResult> ReadAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var path = Resolve(Required(args, "path"));
        var text = await ReadTextAsync(path, cancellationToken).ConfigureAwait(false);
        var lines = text.Split('\n');
        var start = Math.Max(1, ProviderHttp.Int(args, "start_line") ?? 1);
        var end = Math.Min(lines.Length, ProviderHttp.Int(args, "end_line") ?? lines.Length);
        if (start > lines.Length)
        {
            return ToolResult.Error($"{Relative(path)} has only {lines.Length} lines.");
        }

        var output = new StringBuilder();
        var last = Math.Min(end, start + MaxReadLines - 1);
        for (var i = start; i <= last; i++)
        {
            output.Append(i).Append('\t').Append(lines[i - 1]).Append('\n');
            if (output.Length > MaxResultChars)
            {
                last = i;
                break;
            }
        }

        if (last < end)
        {
            output.Append($"… ({lines.Length} lines in all; read from line {last + 1} for more)\n");
        }

        return ToolResult.Ok(output.ToString());
    }

    /// <summary>The text as the user sees it: the open editor's (unsaved changes included), else the disk's; '\n' line breaks.</summary>
    private async Task<string> ReadTextAsync(string path, CancellationToken cancellationToken)
    {
        if (host.OpenDocumentText(path) is { } open)
        {
            return open;
        }

        if (!File.Exists(path))
        {
            throw new ToolArgumentException($"{Relative(path)} does not exist.");
        }

        try
        {
            return LineEndings.Normalize((await fileSystem.ReadTextAsync(path, cancellationToken).ConfigureAwait(false)).Text);
        }
        catch (BinaryFileException)
        {
            throw new ToolArgumentException($"{Relative(path)} is a binary file.");
        }
    }

    private async Task<ToolResult> SearchAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var query = Required(args, "query");
        var options = new SearchOptions(MatchCase: Bool(args, "match_case"), UseRegex: Bool(args, "is_regex"));
        if (options.UseRegex)
        {
            try
            {
                _ = new Regex(query, RegexOptions.None, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException exception)
            {
                return ToolResult.Error($"Invalid regular expression: {exception.Message}");
            }
        }

        var results = new Collector();
        await new WorkspaceSearch(fileSystem).SearchAsync(Root, query, options, results, cancellationToken).ConfigureAwait(false);
        var text = new StringBuilder();
        var count = 0;
        foreach (var file in results.Files)
        {
            foreach (var match in file.Matches)
            {
                if (count++ >= MaxMatches)
                {
                    break;
                }

                text.Append(Relative(file.Path)).Append(':').Append(match.Position.Line + 1).Append(": ").Append(match.Preview.Trim()).Append('\n');
            }
        }

        var total = results.Files.Sum(f => f.Matches.Count);
        if (total > MaxMatches)
        {
            text.Append($"… {total - MaxMatches} more matches; narrow the search.\n");
        }

        return ToolResult.Ok(total == 0 ? $"No matches for {query}." : text.ToString());
    }

    private async Task<ToolResult> EditAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var path = Resolve(Required(args, "path"));
        var oldText = LineEndings.Normalize(Required(args, "old_text", allowEmpty: true));
        var newText = LineEndings.Normalize(Required(args, "new_text", allowEmpty: true));
        var original = await ReadTextAsync(path, cancellationToken).ConfigureAwait(false);
        if (oldText.Length == 0)
        {
            return ToolResult.Error("old_text is empty. To replace a whole file use write_file.");
        }

        var count = Occurrences(original, oldText);
        if (count == 0)
        {
            return ToolResult.Error($"old_text was not found in {Relative(path)}. Read the file again and copy the text exactly (whitespace included, without line-number prefixes).");
        }

        var replaceAll = Bool(args, "replace_all");
        if (count > 1 && !replaceAll)
        {
            return ToolResult.Error($"old_text appears {count} times in {Relative(path)}. Include more surrounding lines to make it unique, or set replace_all.");
        }

        var proposed = replaceAll ? original.Replace(oldText, newText, StringComparison.Ordinal) : ReplaceFirst(original, oldText, newText);
        return await ReviewAsync(new EditProposal(path, original, proposed), cancellationToken).ConfigureAwait(false);
    }

    private async Task<ToolResult> WriteAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var path = Resolve(Required(args, "path"));
        if (Directory.Exists(path))
        {
            return ToolResult.Error($"{Relative(path)} is a folder.");
        }

        var content = LineEndings.Normalize(Required(args, "content", allowEmpty: true));
        var original = File.Exists(path) || host.OpenDocumentText(path) is not null ? await ReadTextAsync(path, cancellationToken).ConfigureAwait(false) : null;
        if (original == content)
        {
            return ToolResult.Ok($"{Relative(path)} already has exactly this content; nothing changed.");
        }

        return await ReviewAsync(new EditProposal(path, original, content), cancellationToken).ConfigureAwait(false);
    }

    private async Task<ToolResult> ReviewAsync(EditProposal proposal, CancellationToken cancellationToken)
    {
        var decision = await host.ReviewEditAsync(proposal, cancellationToken).ConfigureAwait(false);
        var name = Relative(proposal.Path);
        var feedback = string.IsNullOrWhiteSpace(decision.Feedback) ? string.Empty : $" The user said: {decision.Feedback}";
        return decision.Accepted
            ? ToolResult.Ok($"The user accepted the change; {name} was {(proposal.Original is null ? "created" : "updated")}.{feedback}")
            : ToolResult.Error($"The user rejected the change to {name}; it was not applied.{feedback} Ask what they want instead, or try a different approach.");
    }

    private async Task<ToolResult> RunAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var command = Required(args, "command");
        var proposal = new CommandProposal(command, Root);
        if (!await host.ConfirmCommandAsync(proposal, cancellationToken).ConfigureAwait(false))
        {
            return ToolResult.Error("The user declined to run this command. Do not run it again; ask them how to proceed if you need it.");
        }

        var result = await commands.RunAsync(command, Root, CommandTimeout, cancellationToken).ConfigureAwait(false);
        var output = result.Output.Length > MaxResultChars ? "…" + result.Output[^MaxResultChars..] : result.Output;
        var status = result.TimedOut ? $"The command was stopped after {CommandTimeout.TotalMinutes:0} minutes."
            : result.ExitCode is { } code ? $"Exit code {code}." : "The command ended.";
        return new ToolResult($"{status}\n{(output.Length == 0 ? "(no output)" : output)}", result.TimedOut || result.ExitCode is not (0 or null));
    }

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string ReplaceFirst(string text, string oldText, string newText)
    {
        var index = text.IndexOf(oldText, StringComparison.Ordinal);
        return string.Concat(text.AsSpan(0, index), newText, text.AsSpan(index + oldText.Length));
    }

    private static string Required(JsonElement args, string name, bool allowEmpty = false)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new ToolArgumentException($"The argument \"{name}\" is missing (a string is required).");
        }

        var text = value.GetString() ?? string.Empty;
        return allowEmpty || text.Trim().Length > 0 ? text : throw new ToolArgumentException($"The argument \"{name}\" is empty.");
    }

    private static bool Bool(JsonElement args, string name) =>
        args.TryGetProperty(name, out var value) && (value.ValueKind == JsonValueKind.True || (value.ValueKind == JsonValueKind.String && value.GetString() == "true"));

    private sealed class Collector : IProgress<FileSearchResult>
    {
        private readonly List<FileSearchResult> _files = [];

        public IReadOnlyList<FileSearchResult> Files
        {
            get
            {
                lock (_files)
                {
                    return [.. _files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)];
                }
            }
        }

        public void Report(FileSearchResult value)
        {
            lock (_files)
            {
                _files.Add(value);
            }
        }
    }

    private sealed class ToolArgumentException(string message) : Exception(message);
}

/// <summary>A tool's answer for the model; <see cref="IsError"/> marks a failure (shown to the user too).</summary>
public sealed record ToolResult(string Text, bool IsError)
{
    public static ToolResult Ok(string text) => new(text, false);

    public static ToolResult Error(string text) => new("Error: " + text, true);
}
