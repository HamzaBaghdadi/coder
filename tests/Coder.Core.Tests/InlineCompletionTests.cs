using System.Runtime.CompilerServices;
using System.Text.Json;
using Coder.Core.Ai;

namespace Coder.Core.Tests;

public sealed class InlineCompletionTests
{
    private static InlineCompletionContext At(string textWithCaret, string path = "src/Math.cs", string language = "csharp")
    {
        var caret = textWithCaret.IndexOf('|', StringComparison.Ordinal);
        return InlineCompletionPrompt.Context(path, language, textWithCaret.Remove(caret, 1), caret);
    }

    [Fact]
    public void Prompt_marks_the_caret_and_names_the_file_and_language()
    {
        var context = At("int Add(int a, int b)\n{\n    return |\n}\n");

        var request = InlineCompletionPrompt.Build("model-x", context);

        Assert.Equal("model-x", request.Model);
        Assert.Equal(InlineCompletionPrompt.MaxTokens, request.MaxTokens);
        Assert.Empty(request.Tools);
        Assert.Equal(ChatRole.System, request.Messages[0].Role);
        var user = request.Messages[1].Text;
        Assert.Contains("File: src/Math.cs (language: csharp)", user, StringComparison.Ordinal);
        Assert.Contains("    return <CURSOR>\n}\n", user, StringComparison.Ordinal);
    }

    [Fact]
    public void Context_keeps_whole_lines_within_the_limits()
    {
        var line = new string('x', 99) + "\n";
        var text = string.Concat(Enumerable.Repeat(line, 200)); // 20 000 characters
        var caret = 10_000;

        var context = InlineCompletionPrompt.Context("a.txt", "plaintext", text, caret);

        Assert.True(context.Prefix.Length <= InlineCompletionPrompt.MaxPrefixChars);
        Assert.True(context.Suffix.Length <= InlineCompletionPrompt.MaxSuffixChars);
        Assert.Equal(caret - context.Prefix.Length, text.LastIndexOf('\n', caret - context.Prefix.Length - 1) + 1); // starts a line
        Assert.Equal(text[(caret - context.Prefix.Length)..(caret + context.Suffix.Length)], context.Prefix + context.Suffix);
    }

    [Fact]
    public void Context_at_the_start_and_end_of_a_short_file()
    {
        Assert.Equal(new InlineCompletionContext("a", "b", string.Empty, "abc"), InlineCompletionPrompt.Context("a", "b", "abc", 0));
        Assert.Equal(new InlineCompletionContext("a", "b", "abc", string.Empty), InlineCompletionPrompt.Context("a", "b", "abc", 3));
    }

    [Theory]
    [InlineData("a + b;", "a + b;")]
    [InlineData("```csharp\na + b;\n```", "a + b;")]
    [InlineData("Here you go:\n```\na + b;\n```\nThis adds.", "a + b;")]
    [InlineData("<think>Simple sum.</think>\na + b;", "a + b;")]
    [InlineData("<think>Still thinking", null)]
    [InlineData("   \n  ", null)]
    [InlineData("", null)]
    [InlineData("return a + b;", "a + b;")] // the typed part of the line repeated
    [InlineData("    return a + b;", "a + b;")] // with its indentation
    [InlineData("a + b;<CURSOR>", "a + b;")]
    public void Clean_keeps_only_the_new_text(string reply, string? expected)
    {
        var context = At("int Add(int a, int b)\n{\n    return |\n}\n");

        Assert.Equal(expected, InlineCompletionText.Clean(reply, context));
    }

    [Fact]
    public void Clean_drops_the_rest_of_the_line_the_model_repeated()
    {
        var context = At("Console.WriteLine(|);\n");

        Assert.Equal("\"Hello\"", InlineCompletionText.Clean("\"Hello\");", context));
    }

    [Fact]
    public void Clean_drops_closing_lines_that_already_follow()
    {
        var context = At("int Add(int a, int b)\n{\n    |\n}\n");

        Assert.Equal("var sum = a + b;\n    return sum;", InlineCompletionText.Clean("    var sum = a + b;\n    return sum;\n}", context));
    }

    [Fact]
    public void Clean_drops_a_repeated_previous_line()
    {
        var context = At("def area(r):\n    |");

        Assert.Equal("return 3.14 * r * r", InlineCompletionText.Clean("def area(r):\n    return 3.14 * r * r", context));
    }

    [Fact]
    public void Clean_cuts_long_answers()
    {
        var context = At("|");
        var reply = string.Join('\n', Enumerable.Range(1, 40).Select(i => $"line{i}"));

        var text = InlineCompletionText.Clean(reply, context)!;

        Assert.Equal(InlineCompletionText.MaxLines, text.Split('\n').Length);
    }

    [Fact]
    public void Clean_normalizes_line_endings()
    {
        Assert.Equal("a\nb", InlineCompletionText.Clean("a\r\nb\r\n", At("|")));
    }

    [Theory]
    [InlineData("Apples</li>", "ples")] // the word being typed and the closing tag repeated
    [InlineData("ples", "ples")]
    [InlineData("<li>Apples</li>", "ples")]
    public void Clean_handles_text_between_html_tags(string reply, string expected)
    {
        var context = At("<ul>\n  <li>Ap|</li>\n</ul>\n", "index.html", "html");

        Assert.Equal(expected, InlineCompletionText.Clean(reply, context));
    }

    [Fact]
    public async Task Engine_reports_a_model_that_only_thinks()
    {
        var provider = new ScriptedProvider(_ => [new ReasoningDelta("Let me consider the radius..."), new ChatCompleted("length", null)]);

        var error = await Assert.ThrowsAsync<AiProviderException>(() => new InlineCompletionEngine().CompleteAsync(provider, "deepseek-r1", At("|"), TestContext.Current.CancellationToken));

        Assert.Contains("deepseek-r1 spent its answer thinking", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Engine_reports_reasoning_cut_off_inside_think_tags()
    {
        var provider = new ScriptedProvider(_ => [new TextDelta("<think>The user is writing"), new ChatCompleted("length", null)]);

        var error = await Assert.ThrowsAsync<AiProviderException>(() => new InlineCompletionEngine().CompleteAsync(provider, "qwen3", At("|"), TestContext.Current.CancellationToken));

        Assert.Contains("still thinking", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Engine_returns_null_when_a_model_answers_nothing()
    {
        var provider = new ScriptedProvider(_ => ScriptedProvider.Text(string.Empty));

        Assert.Null(await new InlineCompletionEngine().CompleteAsync(provider, "m", At("|"), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("hello world", 5)]
    [InlineData(" world", 6)]
    [InlineData("(a, b)", 1)]
    [InlineData("();", 3)]
    [InlineData("\n    return x;", 11)]
    [InlineData("x", 1)]
    [InlineData(" ", 1)]
    [InlineData("", 0)]
    public void Next_word_takes_spaces_then_a_word_or_punctuation(string text, int expected)
    {
        Assert.Equal(expected, InlineCompletionText.NextWordLength(text));
    }

    [Fact]
    public async Task Engine_returns_the_cleaned_streamed_reply()
    {
        var provider = new ScriptedProvider(_ => [new TextDelta("```cs\nreturn "), new TextDelta("a + b;\n```"), new ChatCompleted("stop", null)]);
        var context = At("int Add(int a, int b)\n{\n    |\n}\n");

        var text = await new InlineCompletionEngine().CompleteAsync(provider, "fake-model", context, TestContext.Current.CancellationToken);

        Assert.Equal("return a + b;", text);
        var request = Assert.Single(provider.Requests);
        Assert.Equal("fake-model", request.Model);
        Assert.Contains(InlineCompletionPrompt.Marker, request.Messages[^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Engine_ignores_reasoning_and_stops_reading_long_replies()
    {
        var sent = 0;
        IEnumerable<ChatStreamEvent> Endless()
        {
            yield return new ReasoningDelta("Let me think.");
            while (true)
            {
                sent++;
                yield return new TextDelta("x();\n");
            }
        }

        var provider = new ScriptedProvider(_ => Endless());

        var text = await new InlineCompletionEngine().CompleteAsync(provider, "m", At("|"), TestContext.Current.CancellationToken);

        Assert.Equal(InlineCompletionText.MaxLines, text!.Split('\n').Length);
        Assert.True(sent < 100, $"read {sent} chunks");
    }

    [Fact]
    public async Task Engine_reports_a_failing_provider()
    {
        var provider = new FailingProvider(new AiProviderException("Fake: 401 invalid key"));

        var error = await Assert.ThrowsAsync<AiProviderException>(() => new InlineCompletionEngine().CompleteAsync(provider, "m", At("|"), TestContext.Current.CancellationToken));

        Assert.Equal("Fake: 401 invalid key", error.Message);
    }

    [Fact]
    public async Task Engine_gives_up_on_a_slow_provider()
    {
        var provider = new SlowProvider(TimeSpan.FromSeconds(30));
        var engine = new InlineCompletionEngine { Timeout = TimeSpan.FromMilliseconds(100) };

        var error = await Assert.ThrowsAsync<AiProviderException>(() => engine.CompleteAsync(provider, "m", At("|"), TestContext.Current.CancellationToken));

        Assert.Contains("took longer", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Engine_cancels_when_the_user_types_on()
    {
        var provider = new SlowProvider(TimeSpan.FromSeconds(30));
        using var typing = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new InlineCompletionEngine().CompleteAsync(provider, "m", At("|"), typing.Token));
    }

    [Fact]
    public void Cache_finds_answers_by_position_and_forgets_the_oldest()
    {
        var cache = new InlineCompletionCache(capacity: 2);
        var a = At("a|");
        var b = At("b|");
        var c = At("c|");
        cache.Add(a, "1");
        cache.Add(b, null);

        Assert.True(cache.TryGet(At("a|"), out var text));
        Assert.Equal("1", text);
        Assert.True(cache.TryGet(b, out var none));
        Assert.Null(none);

        cache.Add(c, "3"); // a is now the least recently used
        Assert.False(cache.TryGet(a, out _));
        Assert.True(cache.TryGet(c, out _));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public async Task Providers_send_the_token_limit()
    {
        using var openAi = new FakeHttpHandler((_, _) => FakeHttpHandler.Sse("""{"choices":[{"delta":{"content":"x"},"finish_reason":"stop"}]}""", "[DONE]"));
        using var ollama = new FakeHttpHandler((_, _) => FakeHttpHandler.Ndjson("""{"message":{"role":"assistant","content":"x"},"done":true}"""));
        using var openAiHttp = new HttpClient(openAi);
        using var ollamaHttp = new HttpClient(ollama);
        var request = InlineCompletionPrompt.Build("m", At("|"));

        await new InlineCompletionEngine().CompleteAsync(AiProviders.Create(openAiHttp, new("o", AiProviderKind.OpenRouter, "OpenRouter", AiProviders.OpenRouterUrl), "k"), "m", At("|"), TestContext.Current.CancellationToken);
        await new InlineCompletionEngine().CompleteAsync(AiProviders.Create(ollamaHttp, new("l", AiProviderKind.Ollama, "Ollama", AiProviders.OllamaUrl), null), "m", At("|"), TestContext.Current.CancellationToken);

        using var openAiBody = JsonDocument.Parse(Assert.Single(openAi.Requests).Body);
        Assert.Equal(request.MaxTokens, openAiBody.RootElement.GetProperty("max_tokens").GetInt32());
        using var ollamaBody = JsonDocument.Parse(Assert.Single(ollama.Requests).Body);
        Assert.Equal(request.MaxTokens, ollamaBody.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32());
    }

    private sealed class FailingProvider(Exception error) : IChatProvider
    {
        public AiProviderConfig Config { get; } = new("fake", AiProviderKind.OpenAiCompatible, "Fake", "http://fake");

        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken) => throw error;

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest chat, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            throw error;
#pragma warning disable CS0162 // an iterator needs a yield
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class SlowProvider(TimeSpan delay) : IChatProvider
    {
        public AiProviderConfig Config { get; } = new("slow", AiProviderKind.Ollama, "Slow", "http://slow");

        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ModelInfo>>([]);

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest chat, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            yield return new TextDelta("late");
        }
    }
}
