using System.Runtime.CompilerServices;
using Coder.Core.Ai;
using Coder.Core.Files;

namespace Coder.Core.Tests;

/// <summary>A provider that answers each request from a script and remembers the requests.</summary>
internal sealed class ScriptedProvider(params Func<ChatRequest, IEnumerable<ChatStreamEvent>>[] replies) : IChatProvider
{
    public List<ChatRequest> Requests { get; } = [];

    public AiProviderConfig Config { get; } = new("fake", AiProviderKind.OpenAiCompatible, "Fake", "http://fake");

    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ModelInfo>>([new ModelInfo("fake-model")]);

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest chat, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // A copy: the loop keeps adding to the same list.
        Requests.Add(chat with { Messages = [.. chat.Messages] });
        await Task.Yield();
        foreach (var item in replies[Requests.Count - 1](chat))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }
    }

    public static IEnumerable<ChatStreamEvent> Text(string text) => [new TextDelta(text), new ChatCompleted("stop", null)];

    public static IEnumerable<ChatStreamEvent> Calls(params ToolCall[] calls) => [new ToolCallsRequested(calls), new ChatCompleted("tool_calls", null)];
}

/// <summary>The user's side of the tools, decided in advance.</summary>
internal sealed class ScriptedHost(bool acceptEdits = true, bool runCommands = true) : IAgentToolHost
{
    public List<EditProposal> Edits { get; } = [];

    public List<CommandProposal> Commands { get; } = [];

    public Dictionary<string, string> OpenDocuments { get; } = [];

    public string? OpenDocumentText(string path) => OpenDocuments.TryGetValue(path, out var text) ? text : null;

    public async Task<EditDecision> ReviewEditAsync(EditProposal proposal, CancellationToken cancellationToken)
    {
        Edits.Add(proposal);
        if (acceptEdits)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(proposal.Path)!);
            await File.WriteAllTextAsync(proposal.Path, proposal.Proposed, cancellationToken);
        }

        return new EditDecision(acceptEdits, acceptEdits ? null : "use tabs");
    }

    public Task<bool> ConfirmCommandAsync(CommandProposal proposal, CancellationToken cancellationToken)
    {
        Commands.Add(proposal);
        return Task.FromResult(runCommands);
    }
}

internal sealed class RecordingRunner : ICommandRunner
{
    public List<string> Ran { get; } = [];

    public Task<CommandResult> RunAsync(string command, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Ran.Add(command);
        return Task.FromResult(new CommandResult(0, "build succeeded", false));
    }
}

public sealed class AgentLoopTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("coder-agent-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private AgentToolbox Toolbox(ScriptedHost host, ICommandRunner? runner = null) => new(_root, new LocalFileSystem(), host, runner ?? new RecordingRunner());

    private static async Task<List<AgentEvent>> Run(AgentLoop loop, List<ChatMessage> conversation)
    {
        var events = new List<AgentEvent>();
        await foreach (var item in loop.RunAsync(conversation, "fake-model", TestContext.Current.CancellationToken))
        {
            events.Add(item);
        }

        return events;
    }

    [Fact]
    public async Task Loop_runs_the_tools_the_model_asks_for_and_sends_the_results_back()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "notes.txt"), "first\nsecond\n", TestContext.Current.CancellationToken);
        var provider = new ScriptedProvider(
            _ => ScriptedProvider.Calls(new ToolCall("c1", "read_file", """{"path":"notes.txt"}""")),
            request => ScriptedProvider.Text($"The file says {(request.Messages[^1].Text.Contains("second", StringComparison.Ordinal) ? "second" : "?")}."));
        var conversation = new List<ChatMessage> { ChatMessage.System("system"), ChatMessage.User("What is in notes.txt?") };

        var events = await Run(new AgentLoop(provider, Toolbox(new ScriptedHost())), conversation);

        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(AgentToolbox.Definitions.Count, provider.Requests[0].Tools.Count);
        var started = Assert.Single(events.OfType<AgentToolStarted>());
        Assert.Equal("Read notes.txt", started.Title);
        var finished = Assert.Single(events.OfType<AgentToolFinished>());
        Assert.False(finished.Result.IsError);
        Assert.Equal("1\tfirst\n2\tsecond\n3\t\n", finished.Result.Text);
        Assert.Equal("The file says second.", string.Concat(events.OfType<AgentTextDelta>().Select(e => e.Text)));

        // system, user, assistant (tool call), tool result, assistant (answer): the next turn continues from here.
        Assert.Equal([ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant], conversation.Select(m => m.Role));
        Assert.Equal("c1", conversation[3].ToolCallId);
        Assert.Equal("The file says second.", conversation[4].Text);
    }

    [Fact]
    public async Task Model_without_tool_support_gets_the_question_again_without_tools()
    {
        var provider = new ScriptedProvider(
            request => request.Tools.Count > 0 ? throw new AiProviderException("gemma does not support tools") : ScriptedProvider.Text("unused"),
            request => ScriptedProvider.Text(request.Tools.Count == 0 ? "Answered without tools." : "?"));
        var loop = new AgentLoop(provider, Toolbox(new ScriptedHost()));

        var events = await Run(loop, [ChatMessage.User("Hi")]);

        Assert.False(loop.UseTools);
        Assert.Contains("can't use tools", Assert.Single(events.OfType<AgentNotice>()).Text);
        Assert.Equal("Answered without tools.", string.Concat(events.OfType<AgentTextDelta>().Select(e => e.Text)));
    }

    [Fact]
    public async Task Loop_stops_after_the_step_limit()
    {
        var call = new ToolCall("c", "list_directory", """{"path":"."}""");
        var provider = new ScriptedProvider(_ => ScriptedProvider.Calls(call), _ => ScriptedProvider.Calls(call), _ => ScriptedProvider.Calls(call));
        var events = await Run(new AgentLoop(provider, Toolbox(new ScriptedHost())) { MaxSteps = 2 }, [ChatMessage.User("Loop")]);

        Assert.Equal(2, provider.Requests.Count);
        Assert.Contains("Stopped after 2 rounds", events.OfType<AgentNotice>().Single().Text);
    }

    [Fact]
    public async Task Stopping_during_a_tool_leaves_every_call_answered()
    {
        using var stop = new CancellationTokenSource();
        var host = new BlockingHost(stop);
        var toolbox = new AgentToolbox(_root, new LocalFileSystem(), host, new RecordingRunner());
        var provider = new ScriptedProvider(_ => ScriptedProvider.Calls(
            new ToolCall("c1", "run_command", """{"command":"dotnet build"}"""),
            new ToolCall("c2", "list_directory", """{"path":"."}""")));
        var conversation = new List<ChatMessage> { ChatMessage.User("Build") };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in new AgentLoop(provider, toolbox).RunAsync(conversation, "m", stop.Token))
            {
            }
        });

        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Tool], conversation.Select(m => m.Role));
        Assert.Equal(["c1", "c2"], conversation.Skip(2).Select(m => m.ToolCallId));
    }

    private sealed class BlockingHost(CancellationTokenSource stop) : IAgentToolHost
    {
        public string? OpenDocumentText(string path) => null;

        public Task<EditDecision> ReviewEditAsync(EditProposal proposal, CancellationToken cancellationToken) => Task.FromResult(new EditDecision(false));

        public async Task<bool> ConfirmCommandAsync(CommandProposal proposal, CancellationToken cancellationToken)
        {
            await stop.CancelAsync(); // the user presses Stop while the command waits for approval
            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }
    }

    [Fact]
    public async Task Tools_stay_inside_the_workspace()
    {
        var toolbox = Toolbox(new ScriptedHost());
        var outside = Path.Combine(Path.GetTempPath(), "secret.txt");

        var result = await toolbox.InvokeAsync(new ToolCall("c", "read_file", $$"""{"path":"../{{Path.GetFileName(_root)}}-other/secret.txt"}"""), TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
        Assert.Contains("outside the workspace", result.Text);

        result = await toolbox.InvokeAsync(new ToolCall("c", "write_file", System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["path"] = outside, ["content"] = "x" })), TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
        Assert.False(File.Exists(outside));
    }

    [Fact]
    public async Task Edits_need_an_exact_unique_match_and_the_users_approval()
    {
        var file = Path.Combine(_root, "src", "app.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "int a = 1;\r\nint b = 1;\r\n", TestContext.Current.CancellationToken);
        var host = new ScriptedHost();
        var toolbox = Toolbox(host);

        var missing = await toolbox.InvokeAsync(new ToolCall("c", "edit_file", """{"path":"src/app.cs","old_text":"int c","new_text":"x"}"""), TestContext.Current.CancellationToken);
        Assert.True(missing.IsError);
        Assert.Contains("not found", missing.Text);

        var ambiguous = await toolbox.InvokeAsync(new ToolCall("c", "edit_file", """{"path":"src/app.cs","old_text":"= 1;","new_text":"= 2;"}"""), TestContext.Current.CancellationToken);
        Assert.Contains("appears 2 times", ambiguous.Text);
        Assert.Empty(host.Edits);

        var edited = await toolbox.InvokeAsync(new ToolCall("c", "edit_file", """{"path":"src/app.cs","old_text":"int b = 1;","new_text":"int b = 2;"}"""), TestContext.Current.CancellationToken);
        Assert.False(edited.IsError);
        Assert.Contains("accepted", edited.Text);
        var proposal = Assert.Single(host.Edits);
        Assert.Equal("int a = 1;\nint b = 1;\n", proposal.Original); // '\n' line breaks, whatever the file uses
        Assert.Equal("int a = 1;\nint b = 2;\n", proposal.Proposed);

        var all = await toolbox.InvokeAsync(new ToolCall("c", "edit_file", """{"path":"src/app.cs","old_text":"int","new_text":"long","replace_all":true}"""), TestContext.Current.CancellationToken);
        Assert.False(all.IsError);
        Assert.Equal("long a = 1;\nlong b = 2;\n", host.Edits[^1].Proposed);
    }

    [Fact]
    public async Task Edits_use_the_open_editors_text_and_rejections_reach_the_model()
    {
        var file = Path.Combine(_root, "a.txt");
        await File.WriteAllTextAsync(file, "on disk", TestContext.Current.CancellationToken);
        var host = new ScriptedHost(acceptEdits: false);
        host.OpenDocuments[file] = "unsaved text";
        var toolbox = Toolbox(host);

        var read = await toolbox.InvokeAsync(new ToolCall("c", "read_file", """{"path":"a.txt"}"""), TestContext.Current.CancellationToken);
        Assert.Equal("1\tunsaved text\n", read.Text);

        var rejected = await toolbox.InvokeAsync(new ToolCall("c", "edit_file", """{"path":"a.txt","old_text":"unsaved","new_text":"saved"}"""), TestContext.Current.CancellationToken);
        Assert.True(rejected.IsError);
        Assert.Contains("rejected", rejected.Text);
        Assert.Contains("use tabs", rejected.Text);
        Assert.Equal("on disk", await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken));

        var created = await new AgentToolbox(_root, new LocalFileSystem(), new ScriptedHost(), new RecordingRunner())
            .InvokeAsync(new ToolCall("c", "write_file", """{"path":"docs/new.md","content":"# New"}"""), TestContext.Current.CancellationToken);
        Assert.Contains("created", created.Text);
        Assert.Equal("# New", await File.ReadAllTextAsync(Path.Combine(_root, "docs", "new.md"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Commands_run_only_when_the_user_approves()
    {
        var runner = new RecordingRunner();
        var declined = await Toolbox(new ScriptedHost(runCommands: false), runner).InvokeAsync(new ToolCall("c", "run_command", """{"command":"rm -rf /"}"""), TestContext.Current.CancellationToken);
        Assert.True(declined.IsError);
        Assert.Contains("declined", declined.Text);
        Assert.Empty(runner.Ran);

        var host = new ScriptedHost();
        var approved = await Toolbox(host, runner).InvokeAsync(new ToolCall("c", "run_command", """{"command":"dotnet build"}"""), TestContext.Current.CancellationToken);
        Assert.False(approved.IsError);
        Assert.Equal(["dotnet build"], runner.Ran);
        Assert.Equal(_root, host.Commands.Single().WorkingDirectory);
        Assert.Equal("Exit code 0.\nbuild succeeded", approved.Text);
    }

    [Fact]
    public async Task Listing_finding_and_searching_files()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "Views"));
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "Views", "MainView.cs"), "class MainView { // TODO: tidy\n}", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "App.cs"), "// todo later", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_root, "bin", "Out.cs"), "TODO", TestContext.Current.CancellationToken);
        var toolbox = Toolbox(new ScriptedHost());

        var listed = await toolbox.InvokeAsync(new ToolCall("c", "list_directory", """{"path":"src"}"""), TestContext.Current.CancellationToken);
        Assert.Equal("Views/\nApp.cs\n", listed.Text);

        var found = await toolbox.InvokeAsync(new ToolCall("c", "find_files", """{"pattern":"src/**/*View.cs"}"""), TestContext.Current.CancellationToken);
        Assert.Equal("src/Views/MainView.cs", found.Text);
        var byName = await toolbox.InvokeAsync(new ToolCall("c", "find_files", """{"pattern":"*.cs"}"""), TestContext.Current.CancellationToken);
        Assert.Equal("src/App.cs\nsrc/Views/MainView.cs", byName.Text); // build output skipped

        var searched = await toolbox.InvokeAsync(new ToolCall("c", "search_text", """{"query":"TODO","match_case":true}"""), TestContext.Current.CancellationToken);
        Assert.Equal("src/Views/MainView.cs:1: class MainView { // TODO: tidy\n", searched.Text);

        var badRegex = await toolbox.InvokeAsync(new ToolCall("c", "search_text", """{"query":"(","is_regex":true}"""), TestContext.Current.CancellationToken);
        Assert.Contains("Invalid regular expression", badRegex.Text);

        var bad = await toolbox.InvokeAsync(new ToolCall("c", "read_file", "{not json"), TestContext.Current.CancellationToken);
        Assert.Contains("not valid JSON", bad.Text);
        var unknown = await toolbox.InvokeAsync(new ToolCall("c", "delete_everything", "{}"), TestContext.Current.CancellationToken);
        Assert.Contains("no tool named", unknown.Text);
    }

    [Fact]
    public async Task Reading_a_range_of_a_long_file()
    {
        await File.WriteAllLinesAsync(Path.Combine(_root, "long.txt"), Enumerable.Range(1, 3000).Select(i => $"line {i}"), TestContext.Current.CancellationToken);
        var toolbox = Toolbox(new ScriptedHost());

        var range = await toolbox.InvokeAsync(new ToolCall("c", "read_file", """{"path":"long.txt","start_line":10,"end_line":12}"""), TestContext.Current.CancellationToken);
        Assert.Equal("10\tline 10\n11\tline 11\n12\tline 12\n", range.Text);

        var whole = await toolbox.InvokeAsync(new ToolCall("c", "read_file", """{"path":"long.txt"}"""), TestContext.Current.CancellationToken);
        Assert.Contains($"read from line {AgentToolbox.MaxReadLines + 1} for more", whole.Text);
    }

    [Fact]
    public async Task Shell_runner_captures_output_and_exit_code()
    {
        var runner = new ShellCommandRunner();
        var result = await runner.RunAsync("echo hello-from-coder", _root, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello-from-coder", result.Output);

        var failed = await runner.RunAsync("exit 3", _root, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        Assert.Equal(3, failed.ExitCode);
    }

    [Fact]
    public void System_prompt_describes_what_the_agent_can_do()
    {
        Assert.Contains("open workspace folder is C:\\work", AgentLoop.SystemPrompt("C:\\work", hasTools: true));
        Assert.Contains("no tool access", AgentLoop.SystemPrompt("C:\\work", hasTools: false));
        Assert.Contains("No folder is open", AgentLoop.SystemPrompt(null, hasTools: false));
    }
}
