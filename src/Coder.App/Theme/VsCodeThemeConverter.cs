using Avalonia.Media;
using Coder.Core.Themes;

namespace Coder.App.Theme;

/// <summary>
/// Turns a VS Code theme into a Coder <see cref="ColorTheme"/>. Each Coder token takes the first of its
/// VS Code color keys that the theme sets, syntax tokens take the theme's TextMate rules, and anything
/// the theme leaves out keeps the built-in theme of the same kind (dark, light or high contrast), so an
/// imported theme always defines every token.
/// </summary>
public static class VsCodeThemeConverter
{
    /// <summary>Coder token → VS Code workbench color keys, most fitting first.</summary>
    private static readonly Dictionary<string, string[]> ColorKeys = new()
    {
        ["Chrome"] = ["titleBar.activeBackground", "activityBar.background", "sideBar.background"],
        ["SideBar"] = ["sideBar.background", "editorGroupHeader.tabsBackground"],
        ["EditorBackground"] = ["editor.background"],
        ["Elevated"] = ["quickInput.background", "editorWidget.background", "dropdown.background", "menu.background"],
        ["StatusBar"] = ["statusBar.background"],
        ["Hover"] = ["list.hoverBackground", "toolbar.hoverBackground"],
        ["Pressed"] = ["toolbar.activeBackground", "keybindingLabel.background"],
        ["Selected"] = ["list.activeSelectionBackground", "list.focusBackground"],
        ["SelectedInactive"] = ["list.inactiveSelectionBackground"],
        ["Border"] = ["sideBar.border", "panel.border", "editorGroup.border", "contrastBorder"],
        ["BorderStrong"] = ["widget.border", "editorWidget.border", "contrastBorder", "panel.border"],
        ["InputBorder"] = ["input.border", "contrastBorder", "focusBorder"],
        ["TextPrimary"] = ["editor.foreground", "foreground"],
        ["TextSecondary"] = ["descriptionForeground", "sideBar.foreground", "foreground"],
        ["TextMuted"] = ["disabledForeground", "editorLineNumber.foreground"],
        ["TextOnAccent"] = ["button.foreground"],
        ["Accent"] = ["focusBorder", "button.background", "textLink.foreground"],
        ["AccentHover"] = ["button.hoverBackground", "textLink.activeForeground"],
        ["AccentBright"] = ["textLink.foreground", "editorCursor.foreground"],
        ["AccentFill"] = ["button.background", "focusBorder"],
        ["AccentFillHover"] = ["button.hoverBackground", "button.background"],
        ["Danger"] = ["errorForeground", "editorError.foreground"],
        ["Warning"] = ["editorWarning.foreground"],
        ["Modified"] = ["tab.activeForeground", "foreground"],
        ["CurrentLine"] = ["editor.lineHighlightBackground"],
        ["TextSelection"] = ["editor.selectionBackground"],
        ["TextSelectionInactive"] = ["editor.inactiveSelectionBackground"],
        ["Caret"] = ["editorCursor.foreground"],
        ["IndentGuide"] = ["editorIndentGuide.background1", "editorIndentGuide.background"],
        ["BracketMatch"] = ["editorBracketMatch.background"],
        ["BracketMatchBorder"] = ["editorBracketMatch.border"],
        ["SearchMatch"] = ["editor.findMatchHighlightBackground"],
        ["SearchMatchCurrent"] = ["editor.findMatchBackground"],
        ["SearchMatchBorder"] = ["editor.findMatchBorder", "editor.findMatchHighlightBorder"],
        ["DiffRemoved"] = ["diffEditor.removedTextBackground"],
        ["DiffInserted"] = ["diffEditor.insertedTextBackground"],
        ["LineNumber"] = ["editorLineNumber.foreground"],
        ["LineNumberActive"] = ["editorLineNumber.activeForeground"],
        ["DiagnosticError"] = ["editorError.foreground", "errorForeground"],
        ["DiagnosticWarning"] = ["editorWarning.foreground"],
        ["DiagnosticInfo"] = ["editorInfo.foreground"],
        ["DiagnosticHint"] = ["editorHint.foreground"],
        ["LinkUnderline"] = ["textLink.foreground"],
        ["InlayHint"] = ["editorInlayHint.foreground"],
        ["InlayHintBackground"] = ["editorInlayHint.background"],
        ["InlineSuggestion"] = ["editorGhostText.foreground"],
        ["GitModified"] = ["gitDecoration.modifiedResourceForeground"],
        ["GitAdded"] = ["gitDecoration.untrackedResourceForeground", "gitDecoration.addedResourceForeground"],
        ["GitDeleted"] = ["gitDecoration.deletedResourceForeground"],
        ["GitConflict"] = ["gitDecoration.conflictingResourceForeground"],
        ["DiffRemovedLine"] = ["diffEditor.removedLineBackground"],
        ["DiffRemovedText"] = ["diffEditor.removedTextBackground"],
        ["DiffInsertedLine"] = ["diffEditor.insertedLineBackground"],
        ["DiffInsertedText"] = ["diffEditor.insertedTextBackground"],
        ["DiffFiller"] = ["diffEditor.diagonalFill"],
        ["ChangeAdded"] = ["editorGutter.addedBackground"],
        ["ChangeModified"] = ["editorGutter.modifiedBackground"],
        ["ChangeDeleted"] = ["editorGutter.deletedBackground"],
        ["ConflictCurrentHeader"] = ["merge.currentHeaderBackground"],
        ["ConflictCurrent"] = ["merge.currentContentBackground"],
        ["ConflictIncomingHeader"] = ["merge.incomingHeaderBackground"],
        ["ConflictIncoming"] = ["merge.incomingContentBackground"],
        ["ConflictAncestor"] = ["merge.commonContentBackground"],
        ["Graph0"] = ["scmGraph.foreground1"],
        ["Graph1"] = ["scmGraph.foreground2"],
        ["Graph2"] = ["scmGraph.foreground3"],
        ["Graph3"] = ["scmGraph.foreground4"],
        ["Graph4"] = ["scmGraph.foreground5"],
        ["Shadow"] = ["widget.shadow"],
        ["TerminalCursor"] = ["terminalCursor.foreground", "editorCursor.foreground"],
        ["ScrollbarThumb"] = ["scrollbarSlider.background"],
        ["ScrollbarThumbHover"] = ["scrollbarSlider.hoverBackground"],
        ["ScrollbarThumbActive"] = ["scrollbarSlider.activeBackground"],
        ["KindFunction"] = ["symbolIcon.functionForeground", "symbolIcon.methodForeground"],
        ["KindField"] = ["symbolIcon.fieldForeground", "symbolIcon.propertyForeground"],
        ["KindVariable"] = ["symbolIcon.variableForeground"],
        ["KindClass"] = ["symbolIcon.classForeground"],
        ["KindInterface"] = ["symbolIcon.interfaceForeground"],
        ["KindEnum"] = ["symbolIcon.enumeratorForeground"],
        ["KindModule"] = ["symbolIcon.moduleForeground", "symbolIcon.namespaceForeground"],
        ["KindKeyword"] = ["symbolIcon.keywordForeground"],
        ["KindSnippet"] = ["symbolIcon.snippetForeground"],
        ["KindEvent"] = ["symbolIcon.eventForeground"],
        ["Ansi0"] = ["terminal.ansiBlack"],
        ["Ansi1"] = ["terminal.ansiRed"],
        ["Ansi2"] = ["terminal.ansiGreen"],
        ["Ansi3"] = ["terminal.ansiYellow"],
        ["Ansi4"] = ["terminal.ansiBlue"],
        ["Ansi5"] = ["terminal.ansiMagenta"],
        ["Ansi6"] = ["terminal.ansiCyan"],
        ["Ansi7"] = ["terminal.ansiWhite"],
        ["Ansi8"] = ["terminal.ansiBrightBlack"],
        ["Ansi9"] = ["terminal.ansiBrightRed"],
        ["Ansi10"] = ["terminal.ansiBrightGreen"],
        ["Ansi11"] = ["terminal.ansiBrightYellow"],
        ["Ansi12"] = ["terminal.ansiBrightBlue"],
        ["Ansi13"] = ["terminal.ansiBrightMagenta"],
        ["Ansi14"] = ["terminal.ansiBrightCyan"],
        ["Ansi15"] = ["terminal.ansiBrightWhite"],
    };

    /// <summary>Syntax token → TextMate scopes to look up, most fitting first (the first one a rule matches wins).</summary>
    private static readonly Dictionary<string, string[]> SyntaxScopes = new()
    {
        ["Syntax.Keyword"] = ["keyword.control", "keyword", "storage.type", "storage.modifier"],
        ["Syntax.Type"] = ["entity.name.type", "entity.name.class", "support.type", "support.class", "storage.type"],
        ["Syntax.Function"] = ["entity.name.function", "support.function", "meta.function-call"],
        ["Syntax.String"] = ["string.quoted", "string"],
        ["Syntax.Number"] = ["constant.numeric"],
        ["Syntax.Comment"] = ["comment.line", "comment"],
        ["Syntax.Constant"] = ["constant.language", "variable.other.constant", "constant"],
        ["Syntax.Operator"] = ["keyword.operator"],
        ["Syntax.Punctuation"] = ["punctuation.separator", "punctuation"],
        ["Syntax.Preprocessor"] = ["meta.preprocessor", "keyword.control.directive", "keyword.other.preprocessor"],
    };

    public static ColorTheme Convert(VsCodeTheme theme, VsCodeThemeEntry entry)
    {
        var baseTheme = BaseFor(entry.UiTheme);
        var overrides = new Dictionary<string, string>();
        foreach (var (token, keys) in ColorKeys)
        {
            if (keys.Select(key => theme.Colors.GetValueOrDefault(key)).FirstOrDefault(value => value is not null) is { } color)
            {
                overrides[token] = color;
            }
        }

        foreach (var (token, scopes) in SyntaxScopes)
        {
            if (scopes.Select(theme.Foreground).FirstOrDefault(value => value is not null) is { } color)
            {
                overrides[token] = color;
            }
        }

        // Plain text: the editor's foreground, or the theme's scope-less rule.
        if (!overrides.ContainsKey("TextPrimary") && theme.DefaultForeground is { } text)
        {
            overrides["TextPrimary"] = text;
        }

        // Surfaces the theme leaves out take its editor background rather than the built-in theme's navy,
        // and hover and pressed layers become a light (or dark) wash over whatever is under them.
        if (theme.Colors.GetValueOrDefault("editor.background") is { } background)
        {
            foreach (var surface in (string[])["Chrome", "SideBar", "StatusBar", "Elevated"])
            {
                overrides.TryAdd(surface, background);
            }
        }

        // The focus halo is the theme's accent, faint.
        if (overrides.TryGetValue("Accent", out var accent) && accent.StartsWith('#') && accent.Length is 7 or 9)
        {
            overrides["FocusRing"] = $"{accent[..7]}/0.28"; // VS Code writes #RRGGBB or #RRGGBBAA
        }

        var wash = entry.IsDark ? "#FFFFFF" : "#000000";
        overrides.TryAdd("Hover", $"{wash}/0.07");
        overrides.TryAdd("Pressed", $"{wash}/0.12");

        // The resting state of animated controls is the hover color at zero alpha (see Palette.HoverClear).
        var hover = overrides["Hover"].Split('/')[0];
        overrides["HoverClear"] = $"#00{hover[^6..]}";

        // Graph lane 6 has no VS Code key: reuse the theme's error color, which stands apart from the others.
        if (overrides.TryGetValue("DiagnosticError", out var error))
        {
            overrides["Graph5"] = error;
        }

        return baseTheme.With(entry.Id, entry.Label, entry.IsDark, overrides);
    }

    private static ColorTheme BaseFor(string uiTheme) => uiTheme switch
    {
        "vs" => ColorThemes.Daylight,
        "hc-black" => ColorThemes.HighContrastDark,
        "hc-light" => ColorThemes.HighContrastLight,
        _ => ColorThemes.MidnightBlue,
    };

    /// <summary>Whether <paramref name="value"/> parses as a color (used by tests to check the table).</summary>
    internal static bool IsColor(string value) => Color.TryParse(value.Split('/')[0], out _);
}
