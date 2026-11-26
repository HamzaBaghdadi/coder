using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Coder.App.Controls;
using Coder.App.Theme;
using Coder.Presentation.LanguageFeatures;

namespace Coder.App.Editor;

/// <summary>
/// The parameter hint above the caret: the signature in the code font with the parameter being typed
/// highlighted, "2/3" when the call has overloads, and the signature's documentation below it.
/// </summary>
public sealed class SignatureHelpCard : Decorator
{
    private readonly SelectableTextBlock _signature = new() { FontFamily = Typography.Code, FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _documentation = new()
    {
        FontSize = Typography.SmallSize,
        Foreground = Palette.TextSecondary,
        TextWrapping = TextWrapping.Wrap,
        MaxLines = 4,
        TextTrimming = TextTrimming.CharacterEllipsis,
        Margin = new Thickness(0, 4, 0, 0),
    };

    public SignatureHelpCard()
    {
        MaxWidth = 600;
        IsVisible = false;
        IsHitTestVisible = false; // never in the way of the text under it
        Child = new ChamferBorder
        {
            Background = Palette.Elevated,
            BorderBrush = Palette.BorderStrong,
            BorderThickness = 1,
            Chamfer = Spacing.ChamferSmall,
            Padding = new Thickness(Spacing.M, 6),
            Child = new StackPanel { Children = { _signature, _documentation } },
        };
    }

    /// <summary>Fills the card from the current help; returns false when there is nothing to show.</summary>
    public bool Show(SignatureHelpViewModel help)
    {
        if (help.Current is not var (signature, parameter, index, count))
        {
            return false;
        }

        var inlines = _signature.Inlines!;
        inlines.Clear();
        if (count > 1)
        {
            inlines.Add(new Run($"{(index + 1).ToString(CultureInfo.InvariantCulture)}/{count.ToString(CultureInfo.InvariantCulture)}  ") { Foreground = Palette.TextMuted });
        }

        var label = signature.Label;
        var (start, end) = parameter >= 0 && parameter < signature.Parameters.Count ? signature.Parameters[parameter] : (0, 0);
        start = Math.Clamp(start, 0, label.Length);
        end = Math.Clamp(end, start, label.Length);
        inlines.Add(new Run(label[..start]) { Foreground = Palette.TextPrimary });
        inlines.Add(new Run(label[start..end]) { Foreground = Palette.AccentBright, FontWeight = FontWeight.SemiBold, TextDecorations = TextDecorations.Underline });
        inlines.Add(new Run(label[end..]) { Foreground = Palette.TextPrimary });

        var documentation = signature.Documentation is { } doc
            ? string.Join(" ", MarkdownBlocks.Parse(doc, isMarkdown: true).Where(b => b.Kind == MarkdownBlockKind.Paragraph).Select(b => b.Text))
            : string.Empty;
        _documentation.Text = documentation;
        _documentation.IsVisible = documentation.Length > 0;
        return true;
    }
}
