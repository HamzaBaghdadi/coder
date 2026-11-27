using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Coder.App.Controls;
using Coder.App.Mvvm;
using Coder.App.Theme;
using Coder.Core.Lsp;
using Coder.Core.Syntax;
using Coder.Presentation.LanguageFeatures;

namespace Coder.App.Workbench;

/// <summary>
/// The Problems panel: every diagnostic the language servers reported, grouped by file. Click or
/// Enter opens the file at the problem.
/// </summary>
public sealed class ProblemsView : Panel
{
    public ProblemsView(ProblemsViewModel problems)
    {
        var list = new ListBox
        {
            ItemsSource = problems.Rows,
            ItemContainerTheme = AppStyles.ListRowTheme,
            ItemTemplate = Templates.Row<ProblemRow>(CreateRow),
            Background = Brushes.Transparent,
        }.Bind(IsVisibleProperty, problems, x => x.IsEmpty, empty => !empty);
        AutomationProperties.SetName(list, "Problems");
        ScrollViewer.SetHorizontalScrollBarVisibility(list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        list.ContainerPrepared += (_, e) => AutomationProperties.SetName(e.Container, e.Container.DataContext switch
        {
            ProblemItemRow item => $"{item.Diagnostic.Severity}: {item.Diagnostic.Message} {item.Position}",
            ProblemFileRow file => $"{file.FileName}, {file.Count} problems",
            _ => string.Empty,
        });
        list.Tapped += async (_, e) =>
        {
            if (e.Source is Visual { DataContext: ProblemRow row })
            {
                await problems.ActivateAsync(row);
            }
        };
        list.OnRowKeyDown(async (_, e) =>
        {
            if (e.Key == Key.Enter && list.SelectedItem is ProblemRow row)
            {
                e.Handled = true;
                await problems.ActivateAsync(row);
            }
        });

        // A quiet empty state: an icon over one line of help text, top-aligned and centered.
        var empty = new StackPanel
        {
            Margin = new Thickness(Spacing.L, Spacing.XL),
            Spacing = Spacing.S,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Children =
            {
                new Icon(Icons.Check, 20) { Foreground = Palette.TextSecondary, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock
                {
                    Classes = { "secondary" },
                    Text = "No problems have been detected in the workspace.",
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                },
            },
        }.Bind(IsVisibleProperty, problems, x => x.IsEmpty);

        Children.Add(list);
        Children.Add(empty);
    }

    private static Control CreateRow(ProblemRow row) => row switch
    {
        ProblemFileRow file => new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(Spacing.M, 0),
            Background = Brushes.Transparent,
            Children =
            {
                FileIcons.File(file.Path),
                new TextBlock { Text = file.FileName, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = file.Folder, FontSize = Typography.SmallSize, Foreground = Palette.TextSecondary, VerticalAlignment = VerticalAlignment.Center },
                new Border
                {
                    MinWidth = 18,
                    Height = 18,
                    Padding = new Thickness(5, 0),
                    CornerRadius = new CornerRadius(9), // a pill: half the height
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
        ProblemItemRow item => ItemRow(item),
        _ => new TextBlock(),
    };

    private static DockPanel ItemRow(ProblemItemRow item)
    {
        var (icon, brush) = item.Diagnostic.Severity switch
        {
            DiagnosticSeverity.Error => (Icons.Error, Palette.DiagnosticError),
            DiagnosticSeverity.Warning => (Icons.Warning, Palette.DiagnosticWarning),
            _ => (Icons.Info, Palette.DiagnosticInfo),
        };
        var source = string.Join(" ", new[] { item.Diagnostic.Source, item.Diagnostic.Code is null ? null : $"({item.Diagnostic.Code})" }.Where(s => s is not null));

        return new DockPanel
        {
            Margin = new Thickness(Spacing.M + 20, 0, Spacing.M, 0),
            Background = Brushes.Transparent,
            Children =
            {
                new Icon(icon, 14) { Foreground = brush, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center }.Dock(Dock.Left),
                new TextBlock
                {
                    Text = $"{source} {item.Position}".Trim(),
                    FontSize = Typography.SmallSize,
                    Foreground = Palette.TextSecondary,
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                }.Dock(Dock.Right),
                new TextBlock
                {
                    Text = item.Diagnostic.Message.ReplaceLineEndings(" "),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            },
        };
    }
}
