using System.Text.Json;

namespace Coder.Core.Themes;

/// <summary>One <c>tokenColors</c> rule of a VS Code theme: the TextMate scope selectors it styles and its color.</summary>
public sealed record TokenColorRule(IReadOnlyList<string> Scopes, string? Foreground);

/// <summary>
/// A VS Code color theme file (<c>*-color-theme.json</c>), read as VS Code reads it: JSON with comments
/// and trailing commas, an <c>include</c> of a base theme (whose values this file overrides), workbench
/// <c>colors</c> by key, and <c>tokenColors</c> for syntax (inline, or in an included JSON file).
/// Colors are normalized to <c>#AARRGGBB</c>.
/// </summary>
public sealed class VsCodeTheme
{
    private const int MaxIncludeDepth = 8;

    private static readonly JsonDocumentOptions Options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private VsCodeTheme(string? name, string? type, Dictionary<string, string> colors, List<TokenColorRule> tokenColors)
    {
        Name = name;
        Type = type;
        Colors = colors;
        TokenColors = tokenColors;
    }

    public string? Name { get; }

    /// <summary>"dark", "light", "hc-black" or "hc-light" when the file says.</summary>
    public string? Type { get; }

    public IReadOnlyDictionary<string, string> Colors { get; }

    /// <summary>In file order, included files first: a later rule wins over an equally specific earlier one.</summary>
    public IReadOnlyList<TokenColorRule> TokenColors { get; }

    /// <exception cref="IOException">The file (or a file it includes) can't be read.</exception>
    /// <exception cref="JsonException">It isn't a theme.</exception>
    public static VsCodeTheme Load(string path) => Load(path, depth: 0);

    public static VsCodeTheme Parse(string json, string? folder = null)
    {
        var colors = new Dictionary<string, string>(StringComparer.Ordinal);
        var rules = new List<TokenColorRule>();
        var (name, type) = Read(json, folder, colors, rules, depth: 0);
        return new VsCodeTheme(name, type, colors, rules);
    }

    /// <summary>
    /// The color of the most specific rule matching <paramref name="scope"/>, like TextMate: a selector
    /// matches a scope that equals it or starts with it followed by a dot ("string" matches
    /// "string.quoted.double"); the longest matching selector wins, and among equals the later rule.
    /// </summary>
    public string? Foreground(string scope)
    {
        string? best = null;
        var bestLength = -1;
        foreach (var rule in TokenColors)
        {
            if (rule.Foreground is null)
            {
                continue;
            }

            foreach (var selector in rule.Scopes)
            {
                if (selector.Length >= bestLength && Matches(selector, scope))
                {
                    best = rule.Foreground;
                    bestLength = selector.Length;
                }
            }
        }

        return best;
    }

    /// <summary>The color of a rule without a scope: the theme's default text color.</summary>
    public string? DefaultForeground => TokenColors.LastOrDefault(rule => rule.Scopes.Count == 0 && rule.Foreground is not null)?.Foreground;

    /// <summary><c>#RGB</c>, <c>#RGBA</c>, <c>#RRGGBB</c> or <c>#RRGGBBAA</c> as <c>#AARRGGBB</c>; null when it isn't one.</summary>
    public static string? NormalizeColor(string? value)
    {
        if (value is not { Length: > 1 } || value[0] != '#' || !value.AsSpan(1).ContainsOnlyHex())
        {
            return null;
        }

        var hex = value[1..].ToUpperInvariant();
        hex = hex.Length switch
        {
            3 or 4 => string.Concat(hex.Select(c => new string(c, 2))),
            6 or 8 => hex,
            _ => string.Empty,
        };

        return hex.Length switch
        {
            6 => $"#FF{hex}",
            8 => $"#{hex[6..]}{hex[..6]}",
            _ => null,
        };
    }

    private static VsCodeTheme Load(string path, int depth)
    {
        var colors = new Dictionary<string, string>(StringComparer.Ordinal);
        var rules = new List<TokenColorRule>();
        var (name, type) = Read(File.ReadAllText(path), Path.GetDirectoryName(path), colors, rules, depth);
        return new VsCodeTheme(name, type, colors, rules);
    }

    private static (string? Name, string? Type) Read(string json, string? folder, Dictionary<string, string> colors, List<TokenColorRule> rules, int depth)
    {
        using var document = JsonDocument.Parse(json, Options);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("A color theme is a JSON object.");
        }

        string? name = null, type = null;

        // The included base theme first, so this file's values override it.
        if (String(root, "include") is { } include && folder is not null && depth < MaxIncludeDepth)
        {
            var included = Path.GetFullPath(Path.Combine(folder, include));
            (name, type) = Read(File.ReadAllText(included), Path.GetDirectoryName(included), colors, rules, depth + 1);
        }

        name = String(root, "name") ?? name;
        type = String(root, "type") ?? type;

        if (root.TryGetProperty("colors", out var colorTable) && colorTable.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in colorTable.EnumerateObject())
            {
                if (NormalizeColor(entry.Value.ValueKind == JsonValueKind.String ? entry.Value.GetString() : null) is { } color)
                {
                    colors[entry.Name] = color;
                }
            }
        }

        if (root.TryGetProperty("tokenColors", out var tokenColors))
        {
            if (tokenColors.ValueKind == JsonValueKind.Array)
            {
                ReadRules(tokenColors, rules);
            }
            else if (tokenColors.ValueKind == JsonValueKind.String && folder is not null && depth < MaxIncludeDepth
                     && tokenColors.GetString() is { } file && file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                // Token colors kept in their own file (.tmTheme files are not read: the base theme's syntax colors stay).
                var path = Path.GetFullPath(Path.Combine(folder, file));
                Read(File.ReadAllText(path), Path.GetDirectoryName(path), new Dictionary<string, string>(), rules, depth + 1);
            }
        }

        return (name, type);
    }

    private static void ReadRules(JsonElement array, List<TokenColorRule> rules)
    {
        foreach (var rule in array.EnumerateArray())
        {
            if (rule.ValueKind != JsonValueKind.Object || !rule.TryGetProperty("settings", out var settings) || settings.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var scopes = new List<string>();
            if (rule.TryGetProperty("scope", out var scope))
            {
                var selectors = scope.ValueKind switch
                {
                    JsonValueKind.String => (scope.GetString() ?? string.Empty).Split(','),
                    JsonValueKind.Array => scope.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.String).Select(s => s.GetString() ?? string.Empty),
                    _ => [],
                };

                // A selector with a space ("source.python keyword") needs the full scope stack, which the
                // highlighter doesn't have: its last part alone would be too broad, so it is left out.
                scopes.AddRange(selectors.Select(s => s.Trim()).Where(s => s.Length > 0 && !s.Contains(' ', StringComparison.Ordinal)));
                if (scopes.Count == 0)
                {
                    continue;
                }
            }

            rules.Add(new TokenColorRule(scopes, NormalizeColor(String(settings, "foreground"))));
        }
    }

    private static bool Matches(string selector, string scope) =>
        scope.StartsWith(selector, StringComparison.Ordinal) && (scope.Length == selector.Length || scope[selector.Length] == '.');

    private static string? String(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>Reads hex strings without allocating.</summary>
internal static class HexExtensions
{
    public static bool ContainsOnlyHex(this ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
