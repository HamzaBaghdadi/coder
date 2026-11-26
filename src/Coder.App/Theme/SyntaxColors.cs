using Avalonia.Media;
using Coder.Core.Syntax;

namespace Coder.App.Theme;

/// <summary>
/// Token colors for the editor, from the current theme (<see cref="ColorThemes"/>): cool tones that sit
/// with the blue brand, warm strings and numbers for contrast, all above 4.5:1 on the editor background.
/// </summary>
public static class SyntaxColors
{
    private static readonly IBrush[] Brushes = BuildTable();

    public static IBrush For(TokenKind kind) => Brushes[(int)kind];

    private static IBrush[] BuildTable()
    {
        var table = new IBrush[Enum.GetValues<TokenKind>().Length];
        table[(int)TokenKind.Text] = Palette.TextPrimary;
        foreach (var kind in Enum.GetValues<TokenKind>().Where(k => k != TokenKind.Text))
        {
            table[(int)kind] = Palette.Token("Syntax." + kind); // one theme token per kind ("Syntax.Keyword"...)
        }

        return table;
    }
}
