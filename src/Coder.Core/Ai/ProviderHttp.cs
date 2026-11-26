using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Coder.Core.Ai;

/// <summary>HTTP plumbing the providers share: sending, turning failures into clear messages, reading streams.</summary>
internal static class ProviderHttp
{
    /// <summary>How long listing models may take before giving up (a server that is not running fails sooner).</summary>
    public static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The request address; a base URL that is not one (typed wrong) is a clear error, not a crash.</summary>
    public static Uri Address(AiProviderConfig config, string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : throw new AiProviderException($"{config.Name}: \"{config.BaseUrl}\" is not a valid address. Use one like {AiProviders.DefaultUrl(config.Kind)}.");

    public static ByteArrayContent Json(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }

        var content = new ByteArrayContent(buffer.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    /// <summary>Sends and returns the response once its headers arrive; any failure becomes an <see cref="AiProviderException"/>.</summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request, AiProviderConfig config, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new AiProviderException(Unreachable(config, exception), null, exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiProviderException($"{config.Name} did not answer in time ({config.BaseUrl}).", null, exception);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                body = string.Empty;
            }

            throw new AiProviderException(Describe(config, response.StatusCode, body), (int)response.StatusCode);
        }
    }

    private static string Unreachable(AiProviderConfig config, HttpRequestException exception)
    {
        var hint = config.Kind switch
        {
            AiProviderKind.Ollama => " Is Ollama running? Start it, or check the address.",
            AiProviderKind.FreeLlmApi => " Is FreeLLMAPI running? Start it (npm start or its desktop app), or check the address.",
            _ => " Check the address and your connection.",
        };
        return $"Could not reach {config.Name} at {config.BaseUrl}: {exception.Message}{hint}";
    }

    /// <summary>The service's own error message when the body has one, with what the status means.</summary>
    public static string Describe(AiProviderConfig config, HttpStatusCode status, string body)
    {
        var message = ErrorMessage(body);
        var meaning = status switch
        {
            HttpStatusCode.Unauthorized => "The API key was refused. Check the key in Coder Agent's providers.",
            HttpStatusCode.Forbidden => "Access was denied for this key.",
            HttpStatusCode.PaymentRequired => "The account is out of credits.",
            HttpStatusCode.NotFound => "Not found: check the base URL and the model name.",
            HttpStatusCode.TooManyRequests => "Rate limited: wait a moment and try again.",
            _ => null,
        };
        var parts = new[] { message, meaning }.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList();
        var text = parts.Count > 0 ? string.Join(" ", parts) : $"HTTP {(int)status} {status}.";
        return $"{config.Name}: {text}";
    }

    /// <summary>Finds the message in OpenAI's <c>{"error":{"message":…}}</c>, Ollama's <c>{"error":"…"}</c> and similar shapes.</summary>
    public static string? ErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return ErrorMessage(document.RootElement) ?? Truncate(body);
        }
        catch (JsonException)
        {
            return body.TrimStart().StartsWith('<') ? null : Truncate(body); // an HTML error page says nothing useful
        }
    }

    public static string? ErrorMessage(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (root.TryGetProperty("error", out var error))
        {
            if (error.ValueKind == JsonValueKind.String)
            {
                return error.GetString();
            }

            if (error.ValueKind == JsonValueKind.Object)
            {
                var message = Str(error, "message");

                // OpenRouter puts the upstream provider's own words in metadata.raw.
                if (error.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object && Str(metadata, "raw") is { Length: > 0 } raw)
                {
                    message = message is null ? Truncate(raw) : $"{message} ({Truncate(raw)})";
                }

                return message;
            }
        }

        return Str(root, "message") ?? Str(root, "detail");
    }

    private static string Truncate(string text)
    {
        text = text.Trim();
        return text.Length <= 400 ? text : text[..400] + "…";
    }

    public static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static int? Int(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    /// <summary>Reads the next line of a streamed body; a dropped connection becomes an <see cref="AiProviderException"/>.</summary>
    public static async ValueTask<string?> ReadLineAsync(StreamReader reader, AiProviderConfig config, CancellationToken cancellationToken)
    {
        try
        {
            return await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException)
        {
            throw new AiProviderException($"{config.Name} closed the connection mid-reply: {exception.Message}", null, exception);
        }
    }

    public static async Task<StreamReader> OpenReaderAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        new(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), Encoding.UTF8);

    public static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, AiProviderConfig config, CancellationToken cancellationToken)
    {
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new AiProviderException($"{config.Name} sent a reply Coder could not read. Is {config.BaseUrl} the right address?", null, exception);
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException)
        {
            throw new AiProviderException($"{config.Name} closed the connection: {exception.Message}", null, exception);
        }
    }

    public static JsonDocument? TryParse(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Writes a JSON schema given as text (a tool's parameters); an invalid one becomes an empty object schema.</summary>
    public static void WriteSchema(Utf8JsonWriter writer, string schema)
    {
        using var parsed = TryParse(schema);
        if (parsed is null)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WriteEndObject();
        }
        else
        {
            parsed.RootElement.WriteTo(writer);
        }
    }

    public static CancellationTokenSource Timeout(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout);
        return source;
    }
}
