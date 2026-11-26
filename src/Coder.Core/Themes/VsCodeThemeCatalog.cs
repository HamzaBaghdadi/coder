using System.Text.Json;

namespace Coder.Core.Themes;

/// <summary>A VS Code theme that can be used: where it came from and its file.</summary>
/// <param name="Id">Stable across extension updates (the extension's id and the theme's label), for settings.</param>
/// <param name="Source">"Dark Modern (VS Code)", the extension's name, or "Theme file".</param>
/// <param name="UiTheme">VS Code's base: "vs-dark", "vs", "hc-black" or "hc-light".</param>
public sealed record VsCodeThemeEntry(string Id, string Label, string Source, string UiTheme, string Path)
{
    public bool IsDark => UiTheme is "vs-dark" or "hc-black";
}

/// <summary>
/// Finds VS Code color themes on this machine, without VS Code running: the themes built into VS Code
/// (Dark Modern, Monokai, Solarized…), themes from installed VS Code extensions, and loose
/// <c>.json</c> theme files dropped in Coder's own themes folder.
/// </summary>
public static class VsCodeThemeCatalog
{
    private static readonly JsonDocumentOptions Options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>
    /// Every theme in <paramref name="extensionFolders"/> (folders holding one folder per extension, each
    /// with a package.json that contributes themes) and in <paramref name="themesFolder"/>. Unreadable
    /// extensions are skipped. When an extension is installed in several versions, the newest folder wins.
    /// </summary>
    public static IReadOnlyList<VsCodeThemeEntry> Discover(IEnumerable<string> extensionFolders, string? themesFolder)
    {
        var entries = new Dictionary<string, VsCodeThemeEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in extensionFolders.Where(Directory.Exists))
        {
            IEnumerable<string> extensions;
            try
            {
                extensions = Directory.EnumerateDirectories(root).Order(StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var extension in extensions)
            {
                foreach (var entry in FromExtension(extension))
                {
                    entries[entry.Id] = entry; // later (higher) versions replace earlier ones
                }
            }
        }

        if (themesFolder is not null && Directory.Exists(themesFolder))
        {
            foreach (var file in Directory.EnumerateFiles(themesFolder, "*.json").Order(StringComparer.OrdinalIgnoreCase))
            {
                if (FromFile(file) is { } entry)
                {
                    entries[entry.Id] = entry;
                }
            }
        }

        return entries.Values.OrderBy(e => e.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Where VS Code keeps its built-in and installed extensions on this machine (the folders that exist).</summary>
    public static IReadOnlyList<string> DefaultExtensionFolders()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string[] installs =
        [
            Path.Combine(local, "Programs", "Microsoft VS Code"),
            Path.Combine(programs, "Microsoft VS Code"),
            "/Applications/Visual Studio Code.app/Contents",
            "/usr/share/code",
        ];
        string[] marketplace =
        [
            // Installed from the marketplace (VS Code, Insiders, VSCodium).
            Path.Combine(home, ".vscode", "extensions"),
            Path.Combine(home, ".vscode-insiders", "extensions"),
            Path.Combine(home, ".vscode-oss", "extensions"),
        ];
        return installs.SelectMany(BuiltInExtensionFolders).Concat(marketplace.Where(Directory.Exists)).ToList();
    }

    /// <summary>
    /// VS Code's own extensions (its themes): in <c>resources/app/extensions</c> of the install, or, in
    /// newer Windows installs, of a folder per version inside it (oldest first, so the newest wins).
    /// </summary>
    private static IEnumerable<string> BuiltInExtensionFolders(string install)
    {
        if (!Directory.Exists(install))
        {
            return [];
        }

        string[] direct = [Path.Combine(install, "resources", "app", "extensions"), Path.Combine(install, "Resources", "app", "extensions")];
        try
        {
            var versioned = Directory.EnumerateDirectories(install)
                .Select(version => Path.Combine(version, "resources", "app", "extensions"))
                .Where(Directory.Exists)
                .OrderBy(Directory.GetLastWriteTimeUtc);
            return direct.Where(Directory.Exists).Concat(versioned).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return direct.Where(Directory.Exists).ToList();
        }
    }

    private static IEnumerable<VsCodeThemeEntry> FromExtension(string folder)
    {
        var manifest = Path.Combine(folder, "package.json");
        if (!File.Exists(manifest))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest), Options);
            var root = document.RootElement;
            if (!root.TryGetProperty("contributes", out var contributes) || !contributes.TryGetProperty("themes", out var themes) || themes.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var strings = LocalizedStrings(folder);
            var extensionId = $"{String(root, "publisher") ?? "vscode"}.{String(root, "name") ?? Path.GetFileName(folder)}";
            var source = Localize(String(root, "displayName"), strings) ?? String(root, "name") ?? Path.GetFileName(folder);
            var builtIn = String(root, "publisher") == "vscode";
            var result = new List<VsCodeThemeEntry>();
            foreach (var theme in themes.EnumerateArray())
            {
                var label = Localize(String(theme, "label"), strings) ?? String(theme, "id");
                var path = String(theme, "path");
                if (label is null || path is null)
                {
                    continue;
                }

                result.Add(new VsCodeThemeEntry(
                    $"vscode:{extensionId}/{label}",
                    label,
                    builtIn ? "Built into VS Code" : source,
                    String(theme, "uiTheme") ?? "vs-dark",
                    Path.GetFullPath(Path.Combine(folder, path))));
            }

            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private static VsCodeThemeEntry? FromFile(string file)
    {
        try
        {
            var theme = VsCodeTheme.Load(file);
            var label = theme.Name ?? Path.GetFileNameWithoutExtension(file);
            var uiTheme = theme.Type switch
            {
                "light" => "vs",
                "hc-black" or "hc-light" => theme.Type,
                _ => "vs-dark",
            };
            return new VsCodeThemeEntry($"file:{Path.GetFileName(file)}", label, "Theme file", uiTheme, file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>"%colorTheme.label%" placeholders are looked up in the extension's package.nls.json.</summary>
    private static string? Localize(string? value, IReadOnlyDictionary<string, string> strings) =>
        value is { Length: > 2 } && value[0] == '%' && value[^1] == '%'
            ? strings.GetValueOrDefault(value[1..^1]) ?? value.Trim('%')
            : value;

    private static Dictionary<string, string> LocalizedStrings(string folder)
    {
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        var file = Path.Combine(folder, "package.nls.json");
        if (!File.Exists(file))
        {
            return strings;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(file), Options);
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            // Plain strings, or { "message": "…", "comment": […] }.
            var text = entry.Value.ValueKind == JsonValueKind.String ? entry.Value.GetString() : String(entry.Value, "message");
            if (text is not null)
            {
                strings[entry.Name] = text;
            }
        }

        return strings;
    }

    private static string? String(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
