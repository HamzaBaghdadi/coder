using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Coder.App.Controls;
using Coder.App.Mvvm;
using Coder.App.Theme;
using Coder.Core.Lsp;
using Coder.Core.Syntax;
using Coder.Presentation.Editors;
using Coder.Presentation.LanguageFeatures;

namespace Coder.App.Editor;

/// <summary>
/// The card shown when the pointer rests on code: the problems reported there, then what the
/// language server says about the symbol (its signature as colored code, then its documentation).
/// </summary>
public sealed class HoverCard : Decorator
{
    private const double MaxCardWidth = 560;
    private const double MaxCardHeight = 320;

    private readonly StackPanel _content = new() { Spacing = Spacing.S, Margin = new Thickness(Spacing.M, Spacing.S) };

    public HoverCard()
    {
        MaxWidth = MaxCardWidth;
        IsVisible = false;
        Child = new ChamferBorder
        {
            Background = Palette.Elevated,
            BorderBrush = Palette.BorderStrong,
            BorderThickness = 1,
            Chamfer = Spacing.ChamferSmall,
            Child = new ScrollViewer
            {
                MaxHeight = MaxCardHeight,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Content = _content,
            },
        };
    }

    /// <summary>Fills the card; returns false when there is nothing to show.</summary>
    public bool Show(IReadOnlyList<DiagnosticMarker> diagnostics, HoverInfo? hover, LanguageDefinition language)
    {
        _content.Children.Clear();
        foreach (var marker in diagnostics)
        {
            _content.Children.Add(DiagnosticRow(marker.Diagnostic));
        }

        var blocks = hover is null ? [] : MarkdownBlocks.Parse(hover.Text, hover.IsMarkdown);
        if (diagnostics.Count > 0 && blocks.Count > 0)
        {
            _content.Children.Add(Rule());
        }

        foreach (var block in blocks)
        {
            _content.Children.Add(block.Kind switch
            {
                MarkdownBlockKind.Code => CodeBlock(block.Text, block.Language, language),
                MarkdownBlockKind.Rule => Rule(),
                _ => new SelectableTextBlock { Text = block.Text, TextWrapping = TextWrapping.Wrap, Foreground = Palette.TextSecondary },
            });
        }

        return _content.Children.Count > 0;
    }

    private static Control DiagnosticRow(Diagnostic diagnostic)
    {
        var (icon, brush) = diagnostic.Severity switch
        {
            DiagnosticSeverity.Error => (Icons.Error, Palette.DiagnosticError),
            DiagnosticSeverity.Warning => (Icons.Warning, Palette.DiagnosticWarning),
            _ => (Icons.Info, Palette.DiagnosticInfo),
        };
        var source = string.Join(" ", new[] { diagnostic.Source, diagnostic.Code is null ? null : $"({diagnostic.Code})" }.Where(s => s is not null));
        var text = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Palette.TextPrimary };
        text.Inlines!.Add(new Run(diagnostic.Message));
        if (source.Length > 0)
        {
            text.Inlines.Add(new Run("  " + source) { Foreground = Palette.TextMuted, FontSize = Typography.SmallSize });
        }

        return new DockPanel
        {
            Children =
            {
                new Icon(icon, 15) { Foreground = brush, Margin = new Thickness(0, 1, Spacing.S, 0), VerticalAlignment = VerticalAlignment.Top }.Dock(Dock.Left),
                text,
            },
        };
    }

    /// <summary>A code block colored with the editor's own lexer (the fence's language, else the file's).</summary>
    private static SelectableTextBlock CodeBlock(string code, string? fenceLanguage, LanguageDefinition fileLanguage) =>
        CodeText.Create(code, Languages.All.FirstOrDefault(l => l.Id == fenceLanguage || l.FileExtensions.Contains("." + fenceLanguage)) ?? fileLanguage);

    private static Border Rule() => new() { Height = 1, Background = Palette.BorderStrong, Margin = new Thickness(0, 2) };
}
