using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Coder.App.Controls;
using Coder.App.Mvvm;
using Coder.App.Theme;
using Coder.Presentation.LanguageFeatures;

namespace Coder.App.Workbench;

/// <summary>
/// The References panel: what Find All References (Shift+F12) found, grouped by file. Each reference
/// shows its line in the code font with the symbol highlighted; click or Enter opens it there, and
/// F4 / Shift+F4 step through them from anywhere.
/// </summary>
public sealed class ReferencesView : DockPanel
{
    public ReferencesView(ReferencesViewModel references)
    {
        var title = new TextBlock
        {
            Margin = new Thickness(Spacing.L, Spacing.S, Spacing.L, Spacing.XS),
            Foreground = Palette.TextSecondary,
            TextTrimming = TextTrimming.CharacterEllipsis,
        }.Bind(TextBlock.TextProperty, references, x => x.Title);

        var list = new ListBox
        {
            ItemsSource = references.Rows,
            ItemContainerTheme = AppStyles.ListRowTheme,
            ItemTemplate = Templates.Row<ReferenceRow>(CreateRow),
            Background = Brushes.Transparent,
        };
        AutomationProperties.SetName(list, "References");
        ScrollViewer.SetHorizontalScrollBarVisibility(list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        list.ContainerPrepared += (_, e) => AutomationProperties.SetName(e.Container, e.Container.DataContext switch
        {
            ReferenceItemRow item => $"{Path.GetFileName(item.Path)} line {item.LineNumber}: {item.Preview}",
            ReferenceFileRow file => $"{file.FileName}, {file.Count} references",
            _ => string.Empty,
        });
        list.Tapped += async (_, e) =>
        {
            if (e.Source is Visual { DataContext: ReferenceRow row })
            {
                await references.ActivateAsync(row);
            }
        };
        list.OnRowKeyDown(async (_, e) =>
        {
            if (e.Key == Key.Enter && list.SelectedItem is ReferenceRow row)
            {
                e.Handled = true;
                await references.ActivateAsync(row);
            }
        });

        // Stepping with F4 moves the highlight in the list too.
        references.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ReferencesViewModel.Current) && references.Current is { } current)
            {
                list.SelectedItem = current;
                list.ScrollIntoView(current);
            }
        };

        Children.Add(title.Dock(Dock.Top));
        Children.Add(list);
    }

    private static Control CreateRow(ReferenceRow row) => row switch
    {
        ReferenceFileRow file => new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(Spacing.M, 0),
            Background = Brushes.Transparent,
            Children =
            {
                FileIcons.File(file.Path),
                new TextBlock { Text = file.FileName, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = file.Folder, FontSize = Typography.SmallSize, Foreground = Palette.TextMuted, VerticalAlignment = VerticalAlignment.Center },
                new Border
                {
                    MinWidth = 18,
                    Height = 18,
                    Padding = new Thickness(5, 0),
                    CornerRadius = new CornerRadius(9),
                    Background = Palette.Pressed,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = file.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        FontSize = Typography.CaptionSize,
                        Foreground = Palette.TextSecondary,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            },
        },
        ReferenceItemRow item => ItemRow(item),
        _ => new TextBlock(),
    };

    /// <summary>"42  var total = Parse(text);" with "Parse" highlighted like a find match.</summary>
    private static DockPanel ItemRow(ReferenceItemRow item)
    {
        var preview = new TextBlock
        {
            FontFamily = Typography.Code,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var text = item.Preview;
        var (start, length) = (item.MatchStart, item.MatchLength);
        preview.Inlines!.Add(new Run(text[..start]) { Foreground = Palette.TextSecondary });
        preview.Inlines.Add(new Run(text.Substring(start, length)) { Foreground = Palette.TextPrimary, Background = Palette.SearchMatch, FontWeight = FontWeight.SemiBold });
        preview.Inlines.Add(new Run(text[(start + length)..]) { Foreground = Palette.TextSecondary });

        return new DockPanel
        {
            Margin = new Thickness(Spacing.M + 20, 0, Spacing.M, 0),
            Background = Brushes.Transparent,
            Children =
            {
                new TextBlock
                {
                    Text = item.LineNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    MinWidth = 36,
                    FontSize = Typography.SmallSize,
                    Foreground = Palette.TextMuted,
                    VerticalAlignment = VerticalAlignment.Center,
                }.Dock(Dock.Left),
                preview,
            },
        };
    }
}
