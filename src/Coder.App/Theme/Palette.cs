using Avalonia.Media;

namespace Coder.App.Theme;

/// <summary>
/// Design tokens: semantic colors that components reference by name, never raw hex values. Each token
/// is one brush for the life of the app; switching themes changes its color, so every control using
/// it follows without being rebuilt. Values come from a <see cref="ColorTheme"/>.
/// <para>
/// Surfaces step from the window frame inwards (each slightly lighter in the dark theme, slightly
/// brighter in the light one) to express depth; color is reserved for meaning.
/// </para>
/// </summary>
public static class Palette
{
    // Declared first: the token fields below register themselves here as they initialize.
    private static readonly Dictionary<string, SolidColorBrush> Tokens = [];
    private static readonly Dictionary<string, SolidColorBrush> IconTints = [];

    /// <summary>The theme the tokens currently show.</summary>
    public static ColorTheme Current { get; private set; } = ColorThemes.MidnightBlue;

    // Surfaces.
    public static readonly SolidColorBrush Chrome = Token(nameof(Chrome));                   // title bar, activity bar
    public static readonly SolidColorBrush SideBar = Token(nameof(SideBar));                 // explorer, search, tab strip
    public static readonly SolidColorBrush EditorBackground = Token(nameof(EditorBackground));
    public static readonly SolidColorBrush Elevated = Token(nameof(Elevated));               // palette, find widget, cards
    public static readonly SolidColorBrush StatusBar = Token(nameof(StatusBar));

    // Interactive state layers.
    public static readonly SolidColorBrush Hover = Token(nameof(Hover));

    /// <summary>
    /// The resting background of animated controls: the hover color at zero alpha. Fading from this
    /// changes only opacity; fading from transparent white would flash gray on the dark theme.
    /// </summary>
    public static readonly SolidColorBrush HoverClear = Token(nameof(HoverClear));
    public static readonly SolidColorBrush Pressed = Token(nameof(Pressed));
    public static readonly SolidColorBrush Selected = Token(nameof(Selected));
    public static readonly SolidColorBrush SelectedInactive = Token(nameof(SelectedInactive));

    // Lines.
    public static readonly SolidColorBrush Border = Token(nameof(Border));
    public static readonly SolidColorBrush BorderStrong = Token(nameof(BorderStrong));
    public static readonly SolidColorBrush InputBorder = Token(nameof(InputBorder));

    // Text.
    public static readonly SolidColorBrush TextPrimary = Token(nameof(TextPrimary));
    public static readonly SolidColorBrush TextSecondary = Token(nameof(TextSecondary));
    public static readonly SolidColorBrush TextMuted = Token(nameof(TextMuted));             // hints and decoration only
    public static readonly SolidColorBrush TextOnAccent = Token(nameof(TextOnAccent));       // on Brand.ButtonGradient

    // Accent (the logo's blue) and feedback.
    public static readonly SolidColorBrush Accent = Token(nameof(Accent));
    public static readonly SolidColorBrush AccentHover = Token(nameof(AccentHover));
    public static readonly SolidColorBrush AccentBright = Token(nameof(AccentBright));       // the logo's cyan highlight
    public static readonly SolidColorBrush Danger = Token(nameof(Danger));
    public static readonly SolidColorBrush Warning = Token(nameof(Warning));
    public static readonly SolidColorBrush Modified = Token(nameof(Modified));

    // Editor layers.
    public static readonly SolidColorBrush CurrentLine = Token(nameof(CurrentLine));
    public static readonly SolidColorBrush TextSelection = Token(nameof(TextSelection));
    public static readonly SolidColorBrush TextSelectionInactive = Token(nameof(TextSelectionInactive));
    public static readonly SolidColorBrush Caret = Token(nameof(Caret));
    public static readonly SolidColorBrush IndentGuide = Token(nameof(IndentGuide));
    public static readonly SolidColorBrush BracketMatch = Token(nameof(BracketMatch));
    public static readonly SolidColorBrush BracketMatchBorder = Token(nameof(BracketMatchBorder));
    public static readonly SolidColorBrush SearchMatch = Token(nameof(SearchMatch));
    public static readonly SolidColorBrush SearchMatchCurrent = Token(nameof(SearchMatchCurrent));
    public static readonly SolidColorBrush SearchMatchBorder = Token(nameof(SearchMatchBorder));
    public static readonly SolidColorBrush DiffRemoved = Token(nameof(DiffRemoved));
    public static readonly SolidColorBrush DiffInserted = Token(nameof(DiffInserted));
    public static readonly SolidColorBrush LineNumber = Token(nameof(LineNumber));
    public static readonly SolidColorBrush LineNumberActive = Token(nameof(LineNumberActive));

    // Diagnostics (squiggles, status bar counts, hover cards, Problems).
    public static readonly SolidColorBrush DiagnosticError = Token(nameof(DiagnosticError));
    public static readonly SolidColorBrush DiagnosticWarning = Token(nameof(DiagnosticWarning));
    public static readonly SolidColorBrush DiagnosticInfo = Token(nameof(DiagnosticInfo));
    public static readonly SolidColorBrush DiagnosticHint = Token(nameof(DiagnosticHint));
    public static readonly SolidColorBrush LinkUnderline = Token(nameof(LinkUnderline));

    // Inlay hints: parameter names and inferred types shown inside the code.
    public static readonly SolidColorBrush InlayHint = Token(nameof(InlayHint));
    public static readonly SolidColorBrush InlayHintBackground = Token(nameof(InlayHintBackground));

    // Git: explorer names, Source Control letters, side-by-side diffs, gutter change markers.
    public static readonly SolidColorBrush GitModified = Token(nameof(GitModified));
    public static readonly SolidColorBrush GitAdded = Token(nameof(GitAdded));
    public static readonly SolidColorBrush GitDeleted = Token(nameof(GitDeleted));
    public static readonly SolidColorBrush GitConflict = Token(nameof(GitConflict));
    public static readonly SolidColorBrush DiffRemovedLine = Token(nameof(DiffRemovedLine));
    public static readonly SolidColorBrush DiffRemovedText = Token(nameof(DiffRemovedText));
    public static readonly SolidColorBrush DiffInsertedLine = Token(nameof(DiffInsertedLine));
    public static readonly SolidColorBrush DiffInsertedText = Token(nameof(DiffInsertedText));
    public static readonly SolidColorBrush DiffFiller = Token(nameof(DiffFiller));
    public static readonly SolidColorBrush ChangeAdded = Token(nameof(ChangeAdded));
    public static readonly SolidColorBrush ChangeModified = Token(nameof(ChangeModified));
    public static readonly SolidColorBrush ChangeDeleted = Token(nameof(ChangeDeleted));

    // Merge conflict blocks: the current (ours) side green, the incoming (theirs) side blue, like VS Code.
    public static readonly SolidColorBrush ConflictCurrentHeader = Token(nameof(ConflictCurrentHeader));
    public static readonly SolidColorBrush ConflictCurrent = Token(nameof(ConflictCurrent));
    public static readonly SolidColorBrush ConflictIncomingHeader = Token(nameof(ConflictIncomingHeader));
    public static readonly SolidColorBrush ConflictIncoming = Token(nameof(ConflictIncoming));
    public static readonly SolidColorBrush ConflictAncestor = Token(nameof(ConflictAncestor));

    /// <summary>Lane colors of the commit graph, used in turn by the lines of development.</summary>
    public static readonly IReadOnlyList<SolidColorBrush> GraphLanes =
        [Token("Graph0"), Token("Graph1"), Token("Graph2"), Token("Graph3"), Token("Graph4"), Token("Graph5")];

    // Overlays and the terminal cursor.
    public static readonly SolidColorBrush Shadow = Token(nameof(Shadow));
    public static readonly SolidColorBrush TerminalCursor = Token(nameof(TerminalCursor));

    // Scroll bars.
    public static readonly SolidColorBrush ScrollbarThumb = Token(nameof(ScrollbarThumb));
    public static readonly SolidColorBrush ScrollbarThumbHover = Token(nameof(ScrollbarThumbHover));
    public static readonly SolidColorBrush ScrollbarThumbActive = Token(nameof(ScrollbarThumbActive));

    // Completion item kinds (the badges in the suggestion list).
    public static readonly SolidColorBrush KindFunction = Token(nameof(KindFunction));
    public static readonly SolidColorBrush KindField = Token(nameof(KindField));
    public static readonly SolidColorBrush KindVariable = Token(nameof(KindVariable));
    public static readonly SolidColorBrush KindClass = Token(nameof(KindClass));
    public static readonly SolidColorBrush KindInterface = Token(nameof(KindInterface));
    public static readonly SolidColorBrush KindEnum = Token(nameof(KindEnum));
    public static readonly SolidColorBrush KindModule = Token(nameof(KindModule));
    public static readonly SolidColorBrush KindKeyword = Token(nameof(KindKeyword));
    public static readonly SolidColorBrush KindSnippet = Token(nameof(KindSnippet));
    public static readonly SolidColorBrush KindEvent = Token(nameof(KindEvent));

    /// <summary>Every token name, for checking that a theme defines them all.</summary>
    public static IReadOnlyCollection<string> TokenNames => Tokens.Keys;

    /// <summary>A brush that belongs to the theme (registered so it follows theme switches).</summary>
    public static SolidColorBrush Token(string name)
    {
        var brush = new SolidColorBrush();
        Tokens[name] = brush;
        Set(brush, Current[name]);
        return brush;
    }

    /// <summary>
    /// A language's icon color (from its definition, tuned for dark backgrounds). On light themes it
    /// is darkened, so a yellow JavaScript icon stays visible on white. One shared brush per color.
    /// </summary>
    public static IBrush IconTint(string hex)
    {
        if (!IconTints.TryGetValue(hex, out var brush))
        {
            brush = new SolidColorBrush(Tint(hex, Current));
            IconTints[hex] = brush;
        }

        return brush;
    }

    /// <summary>Shows <paramref name="theme"/>: every token and icon tint takes its color.</summary>
    public static void Apply(ColorTheme theme)
    {
        Current = theme;
        foreach (var (name, brush) in Tokens)
        {
            Set(brush, theme[name]);
        }

        foreach (var (hex, brush) in IconTints)
        {
            brush.Color = Tint(hex, theme);
        }
    }

    /// <summary>A fixed (non-theme) brush, for colors that do not change with the theme.</summary>
    public static IBrush Solid(string hex, double opacity = 1) => new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse(hex), opacity);

    /// <summary>"#RRGGBB", "#AARRGGBB", optionally with "/opacity".</summary>
    private static void Set(SolidColorBrush brush, string value)
    {
        var slash = value.IndexOf('/', StringComparison.Ordinal);
        brush.Color = Color.Parse(slash < 0 ? value : value[..slash]);
        brush.Opacity = slash < 0 ? 1 : double.Parse(value[(slash + 1)..], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static Color Tint(string hex, ColorTheme theme)
    {
        var color = Color.Parse(hex);
        if (theme.IsDark)
        {
            return color;
        }

        const double Keep = 0.6; // toward black: pastel icons get the contrast a light surface needs
        return Color.FromRgb((byte)(color.R * Keep), (byte)(color.G * Keep), (byte)(color.B * Keep));
    }
}
