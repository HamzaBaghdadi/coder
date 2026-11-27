using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Coder.App.Theme;
using Coder.Core.Lsp;

namespace Coder.App.Editor;

/// <summary>
/// The card beside the suggestion list describing the highlighted item, like VS Code's details
/// widget: its detail as a title ("Emmet Abbreviation"), then its documentation, here the expansion's
/// preview in the code font. Styled as the list card it accompanies.
/// </summary>
public sealed class CompletionDetailsCard : Decorator
{
    public const double MaxCardWidth = 420;
    private const double MaxCardHeight = CompletionWidget.RowHeight * CompletionWidget.VisibleRows + 2;

    private readonly TextBlock _title = new()
    {
        FontFamily = Typography.Ui,
        FontSize = Typography.SmallSize,
        Foreground = Palette.TextSecondary,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private readonly SelectableTextBlock _body = new()
    {
        FontFamily = Typography.Code,
        FontSize = 13,
        Foreground = Palette.TextPrimary,
        TextWrapping = TextWrapping.Wrap,
    };

    public CompletionDetailsCard()
    {
        MaxWidth = MaxCardWidth;
        IsVisible = false;
        var card = new Border
        {
            Background = Palette.Elevated,
            BorderBrush = Palette.BorderStrong,
            BorderThickness = new Thickness(1),
            CornerRadius = Spacing.RadiusLarge,
            Padding = new Thickness(Spacing.M, Spacing.S),
            Child = new ScrollViewer
            {
                MaxHeight = MaxCardHeight - Spacing.S * 2 - 2,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Content = new StackPanel { Spacing = Spacing.S, Children = { _title, _body } },
            },
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        ThemeManager.BindShadow(card, "0 8 24 0");
        Child = card;
    }

    /// <summary>Fills the card from an item; false when it has nothing to say beyond its row.</summary>
    public bool Show(CompletionItem? item)
    {
        if (item?.Documentation is not { Length: > 0 } documentation)
        {
            return false;
        }

        _title.Text = item.Detail;
        _title.IsVisible = item.Detail is { Length: > 0 };
        _body.Text = documentation;
        return true;
    }
}
