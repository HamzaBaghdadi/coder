using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Coder.App.Workbench;
using Coder.Core.Ai;
using Coder.Core.Files;
using Coder.Presentation.Agent;
using Coder.Presentation.Settings;
using Coder.Presentation.Workbench;

namespace Coder.App.Tests;

public sealed partial class WorkbenchUiTests
{
    /// <summary>
    /// AI inline suggestions with a fake provider: a moment after typing stops, the model is asked with a
    /// fill-in-the-middle prompt (file name, language, the text around a caret marker) and its answer shows
    /// as gray text after the caret, here over two lines. Tab inserts it. Typing what the suggestion says
    /// keeps the rest without asking again; Ctrl+Right takes the next word; Esc drops it.
    /// </summary>
    [AvaloniaFact]
    public async Task Inline_suggestion_shows_ghost_text_and_tab_accepts_it()
    {
        var provider = new InlineFakeProvider(
            (_, _) => InlineFakeProvider.Reply("```python\n    if radius < 0:\n        raise ValueError(\"radius must not be negative\")\n    return math.pi * radius ** 2\n```"),
            (_, _) => InlineFakeProvider.Reply("return math.pi * radius ** 2"),
            (_, _) => InlineFakeProvider.Reply(" print(\"done\")"));
        var folder = Directory.CreateTempSubdirectory("coder-inline-").FullName;
        var file = Path.Combine(folder, "circle.py");
        await File.WriteAllTextAsync(file, "import math\n\n\ndef area(radius):\n    \"\"\"The area of a circle.\"\"\"\n    \n", TestContext.Current.CancellationToken);
        var (window, workbench) = CreateInlineWindow(provider);
        try
        {
            workbench.OpenFolder(folder);
            window.Show();
            await workbench.OpenFileAsync(file);
            var tab = workbench.Editors.ActiveEditor!;
            var editor = window.GetVisualDescendants().OfType<Editor.CodeEditor>().First();
            var suggestions = workbench.InlineSuggestions;
            editor.FocusText();
            tab.Editor.SetCaret(tab.Document.GetLineEnd(5));
            Dispatcher.UIThread.RunJobs();

            void Type(string text)
            {
                foreach (var c in text)
                {
                    window.KeyTextInput(c.ToString());
                    Dispatcher.UIThread.RunJobs();
                }

                workbench.Completion.Close(); // word completions may open; they would hide the suggestion
            }

            Assert.Equal(InlineSuggestionStatus.Ready, suggestions.Status);
            var indicator = window.GetVisualDescendants().OfType<Controls.FlatButton>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Inline Suggestions");
            Assert.True(indicator.IsEffectivelyVisible);

            // Typing "if" and pausing: one request, then the gray text after the caret.
            Type("if");
            Assert.Null(editor.ShownInlineSuggestion); // not before the pause
            await WaitUntil(() => editor.ShownInlineSuggestion is not null, () => $"status {suggestions.Status}: {suggestions.Error}");
            Assert.Equal(" radius < 0:\n        raise ValueError(\"radius must not be negative\")\n    return math.pi * radius ** 2", editor.ShownInlineSuggestion);
            var request = Assert.Single(provider.Requests);
            Assert.Equal("test-model", request.Model);
            var prompt = request.Messages[^1].Text;
            Assert.Contains("File: circle.py (language: python)", prompt, StringComparison.Ordinal);
            Assert.Contains("\"\"\"The area of a circle.\"\"\"\n    if<CURSOR>\n", prompt, StringComparison.Ordinal);
            Assert.Contains("code completion engine", request.Messages[0].Text, StringComparison.Ordinal);
            Assert.Equal("    if", tab.Document.GetLineText(5)); // nothing inserted yet
            Save(window, "ai-inline-completion");

            // Tab inserts all of it.
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Null(editor.ShownInlineSuggestion);
            Assert.Equal("    if radius < 0:", tab.Document.GetLineText(5));
            Assert.Equal("        raise ValueError(\"radius must not be negative\")", tab.Document.GetLineText(6));
            Assert.Equal("    return math.pi * radius ** 2", tab.Document.GetLineText(7));
            Assert.Equal(tab.Document.GetLineEnd(7), tab.Editor.Selection.Active);

            // On a new line: typing what it suggests keeps the rest (no new request); Ctrl+Right takes a word.
            tab.Editor.SetCaret(tab.Document.GetLineEnd(7));
            tab.Editor.InsertNewLine();
            await WaitUntil(() => provider.Requests.Count == 2 && editor.ShownInlineSuggestion is not null, () => $"{provider.Requests.Count} requests, status {suggestions.Status}");
            Assert.Equal("return math.pi * radius ** 2", editor.ShownInlineSuggestion);
            Type("ret");
            Assert.Equal("urn math.pi * radius ** 2", editor.ShownInlineSuggestion);
            window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.Control);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("    return", tab.Document.GetLineText(8));
            Assert.Equal(" math.pi * radius ** 2", editor.ShownInlineSuggestion);
            await Task.Delay(500, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, provider.Requests.Count);

            // Esc drops it; the text stays as typed.
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Null(editor.ShownInlineSuggestion);
            Assert.Equal("    return", tab.Document.GetLineText(8));

            // Typing something else drops it too, and Tab indents again when nothing is suggested.
            tab.Editor.SetCaret(tab.Document.GetLineEnd(8));
            Type(" x");
            await WaitUntil(() => editor.ShownInlineSuggestion is not null);
            Type("y");
            Assert.Null(editor.ShownInlineSuggestion);
            suggestions.IsEnabled = false;
            tab.Editor.SetCaret(tab.Document.GetLineStart(1));
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.StartsWith("    ", tab.Document.GetLineText(1), StringComparison.Ordinal);
            Assert.Equal(InlineSuggestionStatus.Off, suggestions.Status);
        }
        finally
        {
            await workbench.Editors.CloseAllAsync();
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// A slow provider shows "…" in the status bar while it works and typing on cancels its request; a
    /// failing one shows a warning with the provider's message and the editor keeps working. The completion
    /// list hides a suggestion (Tab is the list's), and the suggestion is back when the list closes.
    /// </summary>
    [AvaloniaFact]
    public async Task Inline_suggestions_handle_slow_and_failing_providers()
    {
        var cancelled = 0;
        var provider = new InlineFakeProvider(
            (_, token) => InlineFakeProvider.Slow(() => cancelled++, token),
            (_, _) => throw new AiProviderException("Test AI: 401 the key was refused"),
            (_, _) => InlineFakeProvider.Reply("(1, 2)"));
        var (window, workbench) = CreateInlineWindow(provider);
        window.Show();
        workbench.Editors.NewUntitled();
        Dispatcher.UIThread.RunJobs();
        var tab = workbench.Editors.ActiveEditor!;
        var editor = window.GetVisualDescendants().OfType<Editor.CodeEditor>().First();
        var suggestions = workbench.InlineSuggestions;
        suggestions.Delay = TimeSpan.FromMilliseconds(50);
        editor.FocusText();
        Dispatcher.UIThread.RunJobs();

        // Slow: loading, then typing on cancels it.
        window.KeyTextInput("a");
        await WaitUntil(() => suggestions.Status == InlineSuggestionStatus.Loading);
        var indicator = window.GetVisualDescendants().OfType<Controls.FlatButton>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Inline Suggestions");
        Assert.Contains(indicator.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "…");
        window.KeyTextInput("b");
        Dispatcher.UIThread.RunJobs();
        await WaitUntil(() => cancelled == 1, () => $"cancelled {cancelled}");

        // Failing: a warning with the provider's words; the text is untouched.
        await WaitUntil(() => suggestions.Status == InlineSuggestionStatus.Failed, () => $"status {suggestions.Status}, {provider.Requests.Count} requests");
        Assert.Contains("401 the key was refused", suggestions.StatusTip, StringComparison.Ordinal);
        Assert.Contains("401", Assert.IsType<string>(ToolTip.GetTip(indicator)), StringComparison.Ordinal);
        Assert.Equal("ab", tab.Document.GetText());
        Assert.Null(editor.ShownInlineSuggestion);

        // The next request (asked for directly, so the back-off doesn't wait) works again.
        await suggestions.TriggerAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("(1, 2)", editor.ShownInlineSuggestion);
        Assert.Equal(InlineSuggestionStatus.Ready, suggestions.Status);
        Assert.Null(suggestions.Error);

        // An open completion list hides it; closing the list shows it again.
        workbench.Completion.Trigger();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();
        if (workbench.Completion.IsOpen)
        {
            Assert.Null(editor.ShownInlineSuggestion);
            workbench.Completion.Close();
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal("(1, 2)", editor.ShownInlineSuggestion);

        // Clicking the indicator turns suggestions off (and on again).
        indicator.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(suggestions.IsEnabled);
        Assert.Null(editor.ShownInlineSuggestion);
        indicator.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(suggestions.IsEnabled);
        Assert.Equal(3, provider.Requests.Count);
    }

    /// <summary>With no provider ready (no key), nothing is asked and the indicator stays hidden.</summary>
    [AvaloniaFact]
    public async Task Inline_suggestions_wait_for_a_provider()
    {
        var provider = new InlineFakeProvider();
        var (window, workbench) = CreateInlineWindow(provider, withKey: false);
        window.Show();
        workbench.Editors.NewUntitled();
        Dispatcher.UIThread.RunJobs();
        workbench.InlineSuggestions.Delay = TimeSpan.FromMilliseconds(20);
        window.GetVisualDescendants().OfType<Editor.CodeEditor>().First().FocusText();
        window.KeyTextInput("x");
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(provider.Requests);
        Assert.Equal(InlineSuggestionStatus.NoModel, workbench.InlineSuggestions.Status);
        Assert.False(workbench.InlineSuggestions.IsAvailable);

        // A key arrives: ready.
        workbench.Agent!.Providers.Single().SetKey("now-a-key");
        Assert.Equal(InlineSuggestionStatus.Ready, workbench.InlineSuggestions.Status);
    }

    private static (MainWindow Window, WorkbenchViewModel Workbench) CreateInlineWindow(InlineFakeProvider provider, bool withKey = true)
    {
        var settings = new MemorySettingsStore();
        settings.Save(new UserSettings
        {
            AgentProviders = [new AgentProviderSetting { Id = "test", Kind = "openai", Name = "Test AI", BaseUrl = "http://fake/v1" }],
            AgentProvider = "test",
            AgentModel = "test-model",
        });
        var keys = new MemoryApiKeyStore();
        if (withKey)
        {
            keys.Set("test", "test-key");
        }

        var workbench = new WorkbenchViewModel(new LocalFileSystem(), new NoDialogs(), settings);
        workbench.EnableAgent(new AgentServices(new HttpClient(), keys, new RecordingCommandRunner()) { CreateProvider = (_, _) => provider });
        return (new MainWindow(workbench) { Width = 1280, Height = 800 }, workbench);
    }

    /// <summary>A chat provider answering each request from a script (the last answer repeats).</summary>
    private sealed class InlineFakeProvider(params Func<ChatRequest, CancellationToken, IAsyncEnumerable<ChatStreamEvent>>[] replies) : IChatProvider
    {
        public List<ChatRequest> Requests { get; } = [];

        public AiProviderConfig Config { get; } = new("test", AiProviderKind.OpenAiCompatible, "Test AI", "http://fake/v1");

        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ModelInfo>>([new ModelInfo("test-model")]);

        public IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest chat, CancellationToken cancellationToken)
        {
            Requests.Add(chat);
            return replies[Math.Min(Requests.Count, replies.Length) - 1](chat, cancellationToken);
        }

        // Both answer on the UI thread: Avalonia 12.0.0's headless runner loses xUnit's test context when
        // work is posted to it from a thread-pool thread, which breaks every later test at cleanup.
        public static async IAsyncEnumerable<ChatStreamEvent> Reply(string text)
        {
            await Task.CompletedTask;
            yield return new TextDelta(text);
            yield return new ChatCompleted("stop", null);
        }

        /// <summary>Never answers; ends (cancelled) on the thread that cancels it.</summary>
        public static async IAsyncEnumerable<ChatStreamEvent> Slow(Action onCancelled, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            var cancelled = new TaskCompletionSource();
            using (token.Register(() => cancelled.TrySetResult()))
            {
                await cancelled.Task;
            }

            onCancelled();
            token.ThrowIfCancellationRequested();
            yield return new TextDelta("too late");
        }
    }
}
