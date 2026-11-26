namespace Coder.App.Theme;

/// <summary>
/// One color theme: a value for every token (see <see cref="Palette"/>). Values are <c>#RRGGBB</c> or
/// <c>#AARRGGBB</c>, optionally followed by <c>/opacity</c> for translucent layers.
/// </summary>
public sealed class ColorTheme(string id, string name, bool isDark, IReadOnlyDictionary<string, string> colors)
{
    public string Id { get; } = id;

    public string Name { get; } = name;

    public bool IsDark { get; } = isDark;

    public IReadOnlyCollection<string> TokenNames => (IReadOnlyCollection<string>)colors.Keys;

    public string this[string token] => colors.TryGetValue(token, out var value)
        ? value
        : throw new KeyNotFoundException($"The {Name} theme has no color for \"{token}\".");

    /// <summary>A theme with this one's colors except <paramref name="overrides"/>, so it defines every token too.</summary>
    public ColorTheme With(string id, string name, bool isDark, IReadOnlyDictionary<string, string> overrides)
    {
        var merged = new Dictionary<string, string>(colors);
        foreach (var (token, value) in overrides)
        {
            if (!merged.ContainsKey(token))
            {
                throw new ArgumentException($"\"{token}\" is not a theme token.", nameof(overrides)); // a typo would go unnoticed
            }

            merged[token] = value;
        }

        return new ColorTheme(id, name, isDark, merged);
    }
}

/// <summary>
/// The built-in themes, both drawn from the logo's blues. Midnight Blue is near-black navy for long
/// sessions; Daylight is its light counterpart for bright rooms. In both, body text keeps about 13:1
/// contrast and secondary text and syntax colors at least 4.5:1.
/// </summary>
public static class ColorThemes
{
    public static ColorTheme MidnightBlue { get; } = new("dark", "Midnight Blue", isDark: true, new Dictionary<string, string>
    {
        // Surfaces, from the window frame inwards.
        ["Chrome"] = "#06080D", ["SideBar"] = "#090C13", ["EditorBackground"] = "#0C0F17", ["Elevated"] = "#121826", ["StatusBar"] = "#06080D",
        ["Hover"] = "#151C2A", ["HoverClear"] = "#00151C2A", ["Pressed"] = "#1B2435", ["Selected"] = "#2F7BFF/0.16", ["SelectedInactive"] = "#FFFFFF/0.05",
        ["Border"] = "#141A26", ["BorderStrong"] = "#212A3A", ["InputBorder"] = "#323E56",
        ["TextPrimary"] = "#D5DBE5", ["TextSecondary"] = "#97A1B2", ["TextMuted"] = "#768197", ["TextOnAccent"] = "#FFFFFF",
        ["Accent"] = "#3D8BFF", ["AccentHover"] = "#5AA2FF", ["AccentBright"] = "#5AC8FA", ["Danger"] = "#E5484D", ["Warning"] = "#D8A657", ["Modified"] = "#C8D0DC",

        // Editor layers.
        ["CurrentLine"] = "#FFFFFF/0.03", ["TextSelection"] = "#2F7BFF/0.30", ["TextSelectionInactive"] = "#2F7BFF/0.15", ["Caret"] = "#5AC8FA",
        ["IndentGuide"] = "#192030", ["BracketMatch"] = "#3D8BFF/0.16", ["BracketMatchBorder"] = "#3D8BFF/0.55",
        ["SearchMatch"] = "#D8A657/0.22", ["SearchMatchCurrent"] = "#D8A657/0.45", ["SearchMatchBorder"] = "#D8A657/0.75",
        ["DiffRemoved"] = "#E5484D/0.28", ["DiffInserted"] = "#3DD68C/0.22", ["LineNumber"] = "#3E4758", ["LineNumberActive"] = "#B9C3D3",
        ["DiagnosticError"] = "#F2555A", ["DiagnosticWarning"] = "#D8A657", ["DiagnosticInfo"] = "#3D8BFF", ["DiagnosticHint"] = "#97A1B2/0.6", ["LinkUnderline"] = "#5AC8FA",

        // Git: explorer names, Source Control letters, diffs and gutter markers.
        ["GitModified"] = "#E2B86B", ["GitAdded"] = "#6CCB8F", ["GitDeleted"] = "#E5675F", ["GitConflict"] = "#F2555A",
        ["DiffRemovedLine"] = "#E5484D/0.13", ["DiffRemovedText"] = "#E5484D/0.32", ["DiffInsertedLine"] = "#3DD68C/0.11", ["DiffInsertedText"] = "#3DD68C/0.28",
        ["DiffFiller"] = "#FFFFFF/0.025", ["ChangeAdded"] = "#3DD68C/0.85", ["ChangeModified"] = "#3D8BFF/0.9", ["ChangeDeleted"] = "#E5484D/0.9",
        ["ConflictCurrentHeader"] = "#3DD68C/0.32", ["ConflictCurrent"] = "#3DD68C/0.12", ["ConflictIncomingHeader"] = "#3D8BFF/0.36", ["ConflictIncoming"] = "#3D8BFF/0.13", ["ConflictAncestor"] = "#FFFFFF/0.06",
        ["Graph0"] = "#3D8BFF", ["Graph1"] = "#3DD68C", ["Graph2"] = "#E2B86B", ["Graph3"] = "#C792EA", ["Graph4"] = "#56C8D8", ["Graph5"] = "#E5675F",
        ["InlayHint"] = "#8A94A8", ["InlayHintBackground"] = "#FFFFFF/0.06",

        // Overlays and the terminal cursor.
        ["Shadow"] = "#99000000", ["TerminalCursor"] = "#5AC8FA/0.55",
        ["ScrollbarThumb"] = "#FFFFFF/0.13", ["ScrollbarThumbHover"] = "#FFFFFF/0.24", ["ScrollbarThumbActive"] = "#5AC8FA/0.45",

        // Completion item kinds.
        ["KindFunction"] = "#C084FC", ["KindField"] = "#60A5FA", ["KindVariable"] = "#7DD3FC", ["KindClass"] = "#FB923C", ["KindInterface"] = "#2DD4BF",
        ["KindEnum"] = "#FBBF24", ["KindModule"] = "#FACC15", ["KindKeyword"] = "#97A1B2", ["KindSnippet"] = "#4ADE80", ["KindEvent"] = "#F87171",

        // Syntax: cool tones that sit with the blue brand, warm strings and numbers for contrast.
        ["Syntax.Keyword"] = "#B4A2F5", ["Syntax.Type"] = "#6FD0C6", ["Syntax.Function"] = "#7DB7FF", ["Syntax.String"] = "#DFAA7E", ["Syntax.Number"] = "#D9A3BE",
        ["Syntax.Comment"] = "#75829A", ["Syntax.Constant"] = "#D9A3BE", ["Syntax.Operator"] = "#A3ADBD", ["Syntax.Punctuation"] = "#8C96A8", ["Syntax.Preprocessor"] = "#E0C27A",

        // Terminal ANSI colors: readable on the dark background without neon glare.
        ["Ansi0"] = "#1B2435", ["Ansi1"] = "#E5676B", ["Ansi2"] = "#4FCB8E", ["Ansi3"] = "#E2C27A", ["Ansi4"] = "#5B9DFF", ["Ansi5"] = "#B4A2F5", ["Ansi6"] = "#5FC9D9", ["Ansi7"] = "#C8D0DC",
        ["Ansi8"] = "#4A5568", ["Ansi9"] = "#FF8A8E", ["Ansi10"] = "#6FE0A8", ["Ansi11"] = "#F0D492", ["Ansi12"] = "#82B6FF", ["Ansi13"] = "#CDBFFF", ["Ansi14"] = "#7FDDEB", ["Ansi15"] = "#F2F5FA",
    });

    public static ColorTheme Daylight { get; } = new("light", "Daylight", isDark: false, new Dictionary<string, string>
    {
        ["Chrome"] = "#E9EDF3", ["SideBar"] = "#F1F4F8", ["EditorBackground"] = "#FBFCFE", ["Elevated"] = "#FFFFFF", ["StatusBar"] = "#E9EDF3",
        ["Hover"] = "#E1E7F0", ["HoverClear"] = "#00E1E7F0", ["Pressed"] = "#D3DCE8", ["Selected"] = "#1F6FEB/0.14", ["SelectedInactive"] = "#000000/0.05",
        ["Border"] = "#D9E0EA", ["BorderStrong"] = "#C5CEDB", ["InputBorder"] = "#A9B5C7",
        ["TextPrimary"] = "#1A2332", ["TextSecondary"] = "#4A5567", ["TextMuted"] = "#5E6A7D", ["TextOnAccent"] = "#FFFFFF",
        ["Accent"] = "#1A62D6", ["AccentHover"] = "#1553B8", ["AccentBright"] = "#0A78A8", ["Danger"] = "#CF222E", ["Warning"] = "#9A6700", ["Modified"] = "#4A5567",

        ["CurrentLine"] = "#000000/0.035", ["TextSelection"] = "#1F6FEB/0.22", ["TextSelectionInactive"] = "#1F6FEB/0.11", ["Caret"] = "#1F6FEB",
        ["IndentGuide"] = "#E4E9F0", ["BracketMatch"] = "#1F6FEB/0.12", ["BracketMatchBorder"] = "#1F6FEB/0.5",
        ["SearchMatch"] = "#E3B341/0.35", ["SearchMatchCurrent"] = "#E3B341/0.65", ["SearchMatchBorder"] = "#9A6700/0.8",
        ["DiffRemoved"] = "#CF222E/0.18", ["DiffInserted"] = "#1A7F37/0.16", ["LineNumber"] = "#A0AAB8", ["LineNumberActive"] = "#1A2332",
        ["DiagnosticError"] = "#CF222E", ["DiagnosticWarning"] = "#B07800", ["DiagnosticInfo"] = "#1F6FEB", ["DiagnosticHint"] = "#677387/0.6", ["LinkUnderline"] = "#0A78A8",

        ["GitModified"] = "#8A5C00", ["GitAdded"] = "#1A7F37", ["GitDeleted"] = "#CF222E", ["GitConflict"] = "#CF222E",
        ["DiffRemovedLine"] = "#CF222E/0.09", ["DiffRemovedText"] = "#CF222E/0.22", ["DiffInsertedLine"] = "#1A7F37/0.09", ["DiffInsertedText"] = "#1A7F37/0.2",
        ["DiffFiller"] = "#000000/0.03", ["ChangeAdded"] = "#2DA44E", ["ChangeModified"] = "#1F6FEB", ["ChangeDeleted"] = "#CF222E",
        ["ConflictCurrentHeader"] = "#1A7F37/0.22", ["ConflictCurrent"] = "#1A7F37/0.08", ["ConflictIncomingHeader"] = "#1F6FEB/0.24", ["ConflictIncoming"] = "#1F6FEB/0.09", ["ConflictAncestor"] = "#000000/0.05",
        ["Graph0"] = "#1F6FEB", ["Graph1"] = "#1A7F37", ["Graph2"] = "#9A6700", ["Graph3"] = "#8250DF", ["Graph4"] = "#0E7C86", ["Graph5"] = "#CF222E",
        ["InlayHint"] = "#5E6A7D", ["InlayHintBackground"] = "#1A2332/0.06",

        ["Shadow"] = "#2E1A2332", ["TerminalCursor"] = "#1F6FEB/0.45",
        ["ScrollbarThumb"] = "#1A2332/0.18", ["ScrollbarThumbHover"] = "#1A2332/0.32", ["ScrollbarThumbActive"] = "#1A62D6/0.55",

        ["KindFunction"] = "#8250DF", ["KindField"] = "#1F6FEB", ["KindVariable"] = "#0A78A8", ["KindClass"] = "#BC4C00", ["KindInterface"] = "#0B7A75",
        ["KindEnum"] = "#9A6700", ["KindModule"] = "#7D6100", ["KindKeyword"] = "#57606A", ["KindSnippet"] = "#1A7F37", ["KindEvent"] = "#CF222E",

        ["Syntax.Keyword"] = "#6F42C1", ["Syntax.Type"] = "#0B7A75", ["Syntax.Function"] = "#1F5FD1", ["Syntax.String"] = "#9A4A12", ["Syntax.Number"] = "#A3366B",
        ["Syntax.Comment"] = "#646E79", ["Syntax.Constant"] = "#A3366B", ["Syntax.Operator"] = "#4B5563", ["Syntax.Punctuation"] = "#57606A", ["Syntax.Preprocessor"] = "#8A5A00",

        ["Ansi0"] = "#1A2332", ["Ansi1"] = "#CF222E", ["Ansi2"] = "#1A7F37", ["Ansi3"] = "#9A6700", ["Ansi4"] = "#1F6FEB", ["Ansi5"] = "#8250DF", ["Ansi6"] = "#0A78A8", ["Ansi7"] = "#6E7781",
        ["Ansi8"] = "#57606A", ["Ansi9"] = "#A40E26", ["Ansi10"] = "#116329", ["Ansi11"] = "#7D4E00", ["Ansi12"] = "#0550AE", ["Ansi13"] = "#6639BA", ["Ansi14"] = "#0E6470", ["Ansi15"] = "#8C959F",
    });

    /// <summary>
    /// For low vision and bright glare, like VS Code's Dark High Contrast: pure black surfaces, white text,
    /// every region outlined in cyan, focus and matches in orange. Text keeps at least 7:1 contrast.
    /// </summary>
    public static ColorTheme HighContrastDark { get; } = MidnightBlue.With("hc-dark", "High Contrast Dark", isDark: true, new Dictionary<string, string>
    {
        ["Chrome"] = "#000000", ["SideBar"] = "#000000", ["EditorBackground"] = "#000000", ["Elevated"] = "#0A0A0A", ["StatusBar"] = "#000000",
        ["Hover"] = "#262626", ["HoverClear"] = "#00262626", ["Pressed"] = "#383838", ["Selected"] = "#3794FF/0.35", ["SelectedInactive"] = "#FFFFFF/0.14",
        ["Border"] = "#6FC3DF", ["BorderStrong"] = "#6FC3DF", ["InputBorder"] = "#6FC3DF",
        ["TextPrimary"] = "#FFFFFF", ["TextSecondary"] = "#E6E6E6", ["TextMuted"] = "#C8C8C8",
        ["Accent"] = "#F38518", ["AccentHover"] = "#FFA94D", ["AccentBright"] = "#6FC3DF", ["Danger"] = "#F48771", ["Warning"] = "#FFD370", ["Modified"] = "#FFFFFF",
        ["CurrentLine"] = "#FFFFFF/0.08", ["TextSelection"] = "#3794FF/0.55", ["TextSelectionInactive"] = "#3794FF/0.30", ["Caret"] = "#FFFFFF",
        ["IndentGuide"] = "#5A5A5A", ["BracketMatch"] = "#F38518/0.25", ["BracketMatchBorder"] = "#F38518",
        ["SearchMatch"] = "#F38518/0.25", ["SearchMatchCurrent"] = "#F38518/0.55", ["SearchMatchBorder"] = "#F38518",
        ["LineNumber"] = "#A6A6A6", ["LineNumberActive"] = "#F38518",
        ["Syntax.Keyword"] = "#569CD6", ["Syntax.Type"] = "#4EC9B0", ["Syntax.Function"] = "#DCDCAA", ["Syntax.String"] = "#CE9178", ["Syntax.Number"] = "#B5CEA8",
        ["Syntax.Comment"] = "#7CA668", ["Syntax.Constant"] = "#79B8FF", ["Syntax.Operator"] = "#FFFFFF", ["Syntax.Punctuation"] = "#FFFFFF", ["Syntax.Preprocessor"] = "#C586C0",
        ["DiagnosticError"] = "#F48771", ["DiagnosticWarning"] = "#FFD370", ["DiagnosticInfo"] = "#6FC3DF", ["DiagnosticHint"] = "#C8C8C8", ["LinkUnderline"] = "#6FC3DF",
        ["GitModified"] = "#E2C08D", ["GitAdded"] = "#A1E3AD", ["GitDeleted"] = "#F48771", ["GitConflict"] = "#FF8A80",
        ["DiffRemoved"] = "#FF0000/0.45", ["DiffInserted"] = "#33FF2E/0.30", ["DiffRemovedLine"] = "#FF0000/0.22", ["DiffRemovedText"] = "#FF0000/0.50",
        ["DiffInsertedLine"] = "#33FF2E/0.16", ["DiffInsertedText"] = "#33FF2E/0.40", ["DiffFiller"] = "#FFFFFF/0.06",
        ["ChangeAdded"] = "#A1E3AD", ["ChangeModified"] = "#6FC3DF", ["ChangeDeleted"] = "#F48771",
        ["ConflictCurrentHeader"] = "#33FF2E/0.35", ["ConflictCurrent"] = "#33FF2E/0.12", ["ConflictIncomingHeader"] = "#3794FF/0.45", ["ConflictIncoming"] = "#3794FF/0.16", ["ConflictAncestor"] = "#FFFFFF/0.10",
        ["Graph0"] = "#6FC3DF", ["Graph1"] = "#A1E3AD", ["Graph2"] = "#FFD370", ["Graph3"] = "#D7A6FF", ["Graph4"] = "#F38518", ["Graph5"] = "#F48771",
        ["TerminalCursor"] = "#FFFFFF", ["ScrollbarThumb"] = "#FFFFFF/0.35", ["ScrollbarThumbHover"] = "#FFFFFF/0.55", ["ScrollbarThumbActive"] = "#F38518",
    });

    /// <summary>The light counterpart, like VS Code's Light High Contrast: white surfaces, black text, deep blue outlines.</summary>
    public static ColorTheme HighContrastLight { get; } = Daylight.With("hc-light", "High Contrast Light", isDark: false, new Dictionary<string, string>
    {
        ["Chrome"] = "#FFFFFF", ["SideBar"] = "#FFFFFF", ["EditorBackground"] = "#FFFFFF", ["Elevated"] = "#FFFFFF", ["StatusBar"] = "#FFFFFF",
        ["Hover"] = "#E6E6E6", ["HoverClear"] = "#00E6E6E6", ["Pressed"] = "#D1D1D1", ["Selected"] = "#0F4A85/0.18", ["SelectedInactive"] = "#000000/0.08",
        ["Border"] = "#0F4A85", ["BorderStrong"] = "#0F4A85", ["InputBorder"] = "#0F4A85",
        ["TextPrimary"] = "#000000", ["TextSecondary"] = "#1F1F1F", ["TextMuted"] = "#3D3D3D",
        ["Accent"] = "#0F4A85", ["AccentHover"] = "#1460AA", ["AccentBright"] = "#006BBD", ["Danger"] = "#B5200D", ["Warning"] = "#895503", ["Modified"] = "#000000",
        ["CurrentLine"] = "#000000/0.06", ["TextSelection"] = "#0F4A85/0.30", ["TextSelectionInactive"] = "#0F4A85/0.15", ["Caret"] = "#000000",
        ["IndentGuide"] = "#A5A5A5", ["BracketMatch"] = "#0F4A85/0.15", ["BracketMatchBorder"] = "#0F4A85",
        ["SearchMatch"] = "#B5200D/0.15", ["SearchMatchCurrent"] = "#B5200D/0.35", ["SearchMatchBorder"] = "#B5200D",
        ["LineNumber"] = "#505050", ["LineNumberActive"] = "#000000",
        ["Syntax.Keyword"] = "#0F4A85", ["Syntax.Type"] = "#185E73", ["Syntax.Function"] = "#5E2CBC", ["Syntax.String"] = "#A31515", ["Syntax.Number"] = "#096D48",
        ["Syntax.Comment"] = "#515151", ["Syntax.Constant"] = "#0F4A85", ["Syntax.Operator"] = "#000000", ["Syntax.Punctuation"] = "#000000", ["Syntax.Preprocessor"] = "#B5200D",
        ["DiagnosticError"] = "#B5200D", ["DiagnosticWarning"] = "#895503", ["DiagnosticInfo"] = "#0F4A85", ["DiagnosticHint"] = "#3D3D3D", ["LinkUnderline"] = "#0F4A85",
        ["GitModified"] = "#895503", ["GitAdded"] = "#374E06", ["GitDeleted"] = "#AD0707", ["GitConflict"] = "#AD0707",
        ["ChangeAdded"] = "#374E06", ["ChangeModified"] = "#0F4A85", ["ChangeDeleted"] = "#AD0707",
        ["Graph0"] = "#0F4A85", ["Graph1"] = "#374E06", ["Graph2"] = "#895503", ["Graph3"] = "#5E2CBC", ["Graph4"] = "#006BBD", ["Graph5"] = "#AD0707",
        ["TerminalCursor"] = "#000000", ["ScrollbarThumb"] = "#000000/0.30", ["ScrollbarThumbHover"] = "#000000/0.45", ["ScrollbarThumbActive"] = "#0F4A85",
    });

    public static IReadOnlyList<ColorTheme> All { get; } = [MidnightBlue, Daylight, HighContrastDark, HighContrastLight];

    /// <summary>The built-in theme with this id, or null.</summary>
    public static ColorTheme? Find(string? id) => All.FirstOrDefault(t => t.Id == id);

    public static ColorTheme ById(string? id) => Find(id) ?? MidnightBlue;
}
