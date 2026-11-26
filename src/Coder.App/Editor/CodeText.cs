using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Coder.App.Theme;
using Coder.Core.Syntax;

namespace Coder.App.Editor;

/// <summary>Code shown outside the editor (hovers, chat replies), colored with the editor's own lexer.</summary>
public static class CodeText
{
    /// <summary>The language a Markdown fence names ("csharp", "cs", "ts"…), or null when Coder does not know it.</summary>
    public static LanguageDefinition? FindLanguage(string? fence)
    {
        if (string.IsNullOrWhiteSpace(fence))
        {
            return null;
        }

        var name = fence.Trim().ToLowerInvariant();
        return Languages.All.FirstOrDefault(l => l.Id == name || l.FileExtensions.Contains("." + name))
            ?? name switch
            {
                "c#" or "cs" => Languages.All.FirstOrDefault(l => l.Id == "csharp"),
                "js" => Languages.All.FirstOrDefault(l => l.Id == "javascript"),
                "ts" => Languages.All.FirstOrDefault(l => l.Id == "typescript"),
                "py" => Languages.All.FirstOrDefault(l => l.Id == "python"),
                "sh" or "bash" or "shell" or "zsh" => Languages.All.FirstOrDefault(l => l.Id == "shellscript"),
                "ps" or "ps1" or "pwsh" => Languages.All.FirstOrDefault(l => l.Id == "powershell"),
                _ => null,
            };
    }

    /// <summary>A selectable block of colored code.</summary>
    public static SelectableTextBlock Create(string code, LanguageDefinition language, double fontSize = 12.5, TextWrapping wrapping = TextWrapping.Wrap)
    {
        var lexer = new LineLexer(language);
        var block = new SelectableTextBlock { FontFamily = Typography.Code, FontSize = fontSize, TextWrapping = wrapping };
        var state = LexState.Normal;
        var tokens = new List<Token>();
        var lines = code.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            tokens.Clear();
            state = lexer.Tokenize(line, state, tokens);
            var column = 0;
            foreach (var token in tokens)
            {
                if (token.Start > column)
                {
                    block.Inlines!.Add(new Run(line[column..token.Start]) { Foreground = SyntaxColors.For(TokenKind.Text) });
                }

                block.Inlines!.Add(new Run(line.Substring(token.Start, token.Length)) { Foreground = SyntaxColors.For(token.Kind) });
                column = token.Start + token.Length;
            }

            if (column < line.Length)
            {
                block.Inlines!.Add(new Run(line[column..]) { Foreground = SyntaxColors.For(TokenKind.Text) });
            }

            if (i < lines.Length - 1)
            {
                block.Inlines!.Add(new LineBreak());
            }
        }

        return block;
    }
}
