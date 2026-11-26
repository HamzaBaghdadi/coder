namespace Coder.Presentation.Settings;

/// <summary>Everything Coder remembers between sessions.</summary>
public sealed record UserSettings
{
    public const double DefaultEditorFontSize = 14;
    public const int MaxRecentFolders = 8;

    /// <summary>Most recent first. The first entry is reopened on startup.</summary>
    public IReadOnlyList<string> RecentFolders { get; set; } = [];

    public double EditorFontSize { get; set; } = DefaultEditorFontSize;

    public bool IsSideBarVisible { get; set; } = true;

    public double SideBarWidth { get; set; } = DefaultSideBarWidth;

    public const double DefaultSideBarWidth = 260;

    /// <summary>The width double-clicking the side bar's edge returns it to.</summary>
    public double SideBarDefaultWidth { get; set; } = DefaultSideBarWidth;

    /// <summary>Where the primary side bar sits: "left" or "right". The secondary side bar takes the other side.</summary>
    public string SideBarLocation { get; set; } = "left";

    /// <summary>Where the activity bar sits: "side" (beside the side bar), "top", "bottom" (of the side bar) or "hidden".</summary>
    public string ActivityBarLocation { get; set; } = "side";

    /// <summary>GPU rendering (smoother) or software rendering (about half the memory). Applies on restart.</summary>
    public bool GpuAcceleration { get; set; } = true;

    /// <summary>"dark" (Midnight Blue), "light" (Daylight) or "system" (follow the operating system).</summary>
    public string ColorTheme { get; set; } = "dark";

    /// <summary>Who last changed the caret's line, shown at the end of it ("Git: Toggle Line Blame").</summary>
    public bool InlineBlame { get; set; } = true;

    /// <summary>Parameter names and inferred types shown inside the code ("View: Toggle Inlay Hints").</summary>
    public bool InlayHints { get; set; } = true;

    /// <summary>The code in miniature on the editor's right edge ("View: Toggle Minimap").</summary>
    public bool ShowMinimap { get; set; } = true;

    /// <summary>The minimap draws words in their colors instead of one bar per line.</summary>
    public bool MinimapRenderCharacters { get; set; }

    /// <summary>The explorer selects (and opens the folders to) the file being edited.</summary>
    public bool ExplorerAutoReveal { get; set; } = true;

    /// <summary>Coder's extension registry, github.com/HamzaBaghdadi/coder-extensions (publishing is a pull request).</summary>
    public const string DefaultExtensionRegistry = "https://raw.githubusercontent.com/HamzaBaghdadi/coder-extensions/main/index.json";

    /// <summary>Coder's own extension registry (an index.json address, or a file); empty turns it off.</summary>
    public string ExtensionRegistry { get; set; } = DefaultExtensionRegistry;

    /// <summary>Offer color themes from Open VSX in the Extensions view.</summary>
    public bool OpenVsx { get; set; } = true;

    /// <summary>The shell new terminals start ("pwsh", "powershell", "cmd", "git-bash", "wsl"); empty for PowerShell.</summary>
    public string TerminalProfile { get; set; } = string.Empty;

    public WindowPlacement? Window { get; set; }

    /// <summary>Language servers to use instead of (or in addition to) the built-in ones.</summary>
    public IReadOnlyList<LanguageServerSetting> LanguageServers { get; set; } = [];

    /// <summary>
    /// Coder Agent's chat services (their keys are kept encrypted elsewhere, never here). Null until the
    /// user changes them: FreeLLMAPI, OpenRouter and Ollama are offered.
    /// </summary>
    public IReadOnlyList<AgentProviderSetting>? AgentProviders { get; set; }

    /// <summary>The provider id Coder Agent chats with.</summary>
    public string AgentProvider { get; set; } = string.Empty;

    /// <summary>The model Coder Agent chats with (the provider's model id).</summary>
    public string AgentModel { get; set; } = string.Empty;
}

/// <summary>
/// A chat service for Coder Agent, e.g. <c>{ "id": "openrouter", "kind": "openrouter", "name": "OpenRouter",
/// "baseUrl": "https://openrouter.ai/api/v1" }</c>. Kinds: "openai" (any OpenAI-compatible server),
/// "openrouter", "freellmapi" and "ollama".
/// </summary>
public sealed record AgentProviderSetting
{
    public string Id { get; set; } = string.Empty;

    public string Kind { get; set; } = "openai";

    public string Name { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = string.Empty;
}

/// <summary>
/// A language server from settings.json, e.g.
/// <c>{ "name": "pylsp", "languages": ["python"], "command": "pylsp" }</c>. It replaces the built-in
/// server for those languages.
/// </summary>
public sealed record LanguageServerSetting
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Language ids, as in the status bar's language (csharp, python, typescript...).</summary>
    public IReadOnlyList<string> Languages { get; set; } = [];

    /// <summary>The executable: a name found on PATH, or a full path.</summary>
    public string Command { get; set; } = string.Empty;

    public IReadOnlyList<string> Arguments { get; set; } = [];

    /// <summary>Files or folders that mark a project root (<c>*.sln</c> patterns allowed).</summary>
    public IReadOnlyList<string> RootMarkers { get; set; } = [];
}

/// <summary>Window position and size in device-independent pixels.</summary>
public sealed record WindowPlacement(int X, int Y, double Width, double Height, bool IsMaximized);

/// <summary>Loads and saves <see cref="UserSettings"/>; implemented by the app layer.</summary>
public interface ISettingsStore
{
    /// <summary>Returns saved settings, or defaults when none exist or the file is unreadable.</summary>
    UserSettings Load();

    void Save(UserSettings settings);

    /// <summary>Where the settings live, for "Open Settings (JSON)"; null when not stored in a file.</summary>
    string? FilePath => null;
}

/// <summary>Compares server settings by value (their lists included), to notice real changes.</summary>
public sealed class LanguageServerSettingComparer : IEqualityComparer<LanguageServerSetting>
{
    public static LanguageServerSettingComparer Instance { get; } = new();

    public bool Equals(LanguageServerSetting? x, LanguageServerSetting? y) =>
        ReferenceEquals(x, y) || (x is not null && y is not null && x.Name == y.Name && x.Command == y.Command
            && x.Languages.SequenceEqual(y.Languages) && x.Arguments.SequenceEqual(y.Arguments) && x.RootMarkers.SequenceEqual(y.RootMarkers));

    public int GetHashCode(LanguageServerSetting obj) => HashCode.Combine(obj.Name, obj.Command);
}
