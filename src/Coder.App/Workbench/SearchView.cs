using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Coder.App.Controls;
using Coder.App.Mvvm;
using Coder.App.Theme;
using Coder.Presentation.Mvvm;
using Coder.Presentation.Search;

namespace Coder.App.Workbench;

/// <summary>
/// Search in files: a query field with options, a live summary, and results grouped by file.
/// Enter in the field moves to the results; Enter on a result opens it at the exact match.
/// </summary>
public sealed class SearchView : DockPanel
{
    private const double MatchIndent = 34;

    private readonly SearchViewModel _search;
    private readonly TextBox _query;
    private readonly ListBox _results;

    public SearchView(SearchViewModel search)
    {
        _search = search;
        Background = Palette.SideBar;

        _query = new TextBox
        {
            Height = 30,
            MinHeight = 30,
            Padding = new Thickness(8, 5),
            PlaceholderText = "Search",
            FontFamily = Typography.Code,
            VerticalContentAlignment = VerticalAlignment.Center,
            InnerRightContent = SearchOptionToggles.Create(search),
        }.Bind(TextBox.TextProperty, search, x => x.Query, Avalonia.Data.BindingMode.TwoWay);
        AutomationProperties.SetName(_query, "Search in files");
        _query.AddHandler(KeyDownEvent, OnQueryKeyDown, RoutingStrategies.Tunnel);

        var replacement = new TextBox
        {
            Height = 30,
            MinHeight = 30,
            Padding = new Thickness(8, 5),
            PlaceholderText = "Replace",
            FontFamily = Typography.Code,
            VerticalContentAlignment = VerticalAlignment.Center,
        }.Bind(TextBox.TextProperty, search, x => x.Replacement, Avalonia.Data.BindingMode.TwoWay);
        AutomationProperties.SetName(replacement, "Replace in files");
        replacement.AddHandler(KeyDownEvent, OnReplacementKeyDown, RoutingStrategies.Tunnel);

        var replaceAll = new FlatButton(Icons.ReplaceAll, "Replace All (Ctrl+Alt+Enter)")
        {
            Width = 28,
            Height = 28,
            Margin = new Thickness(4, 0, 0, 0),
            Command = new RelayCommand(() => _ = search.ReplaceAllAsync()),
        }.Bind(IsEnabledProperty, search, x => x.CanReplaceAll);

        var replaceRow = new DockPanel
        {
            Margin = new Thickness(0, 4, 0, 0),
            Children = { replaceAll.Dock(Dock.Right), replacement },
        }.Bind(IsVisibleProperty, search, x => x.IsReplaceVisible);

        var expander = new FlatButton
        {
            Width = 20,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0, 0, 4, 0),
            Content = Chevron.For(search, x => x.IsReplaceVisible),
            Command = new RelayCommand(() => search.IsReplaceVisible = !search.IsReplaceVisible),
        };
        AutomationProperties.SetName(expander, "Toggle Replace (Ctrl+Shift+H)");
        ToolTip.SetTip(expander, "Toggle Replace (Ctrl+Shift+H)");

        var fields = new DockPanel
        {
            Margin = new Thickness(Spacing.XS, 0, Spacing.M, 0),
            Children =
            {
                expander.Dock(Dock.Left),
                new StackPanel { Children = { _query, replaceRow } },
            },
        };

        var summary = new TextBlock
        {
            FontSize = Typography.SmallSize,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(Spacing.L, Spacing.S, Spacing.L, Spacing.XS),
        }
        .Bind(TextBlock.TextProperty, search, x => x.Summary)
        .Bind(TextBlock.ForegroundProperty, search, x => x.HasError, hasError => hasError ? Palette.Danger : Palette.TextSecondary);

        _results = new ListBox
        {
            ItemsSource = search.Rows,
            ItemContainerTheme = AppStyles.ListRowTheme,
            ItemTemplate = Templates.Row<SearchRowViewModel>(CreateRow),
        }.Bind(ListBox.SelectedItemProperty, search, x => x.SelectedRow, Avalonia.Data.BindingMode.TwoWay);
        AutomationProperties.SetName(_results, "Search results");
        ScrollViewer.SetHorizontalScrollBarVisibility(_results, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        _results.OnRowKeyDown(OnResultsKeyDown);
        _results.Tapped += async (_, e) =>
        {
            if (e.Source is Visual { DataContext: SearchRowViewModel row })
            {
                await _search.ActivateAsync(row);
            }
        };

        Children.Add(SideBarHeader.Create("SEARCH",
            new FlatButton(Icons.Close, "Clear Search Results") { Command = new RelayCommand(search.Clear) }).Dock(Dock.Top));
        Children.Add(fields.Dock(Dock.Top));
        Children.Add(summary.Dock(Dock.Top));
        Children.Add(_results);

        search.FocusRequested += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            _query.Focus();
            _query.SelectAll();
        });
    }

    private Control CreateRow(SearchRowViewModel row) => row switch
    {
        SearchFileRowViewModel file => CreateFileRow(file),
        SearchMatchRowViewModel match => CreateMatchRow(match),
        _ => new TextBlock(),
    };

    private static Control CreateFileRow(SearchFileRowViewModel file)
    {
        var chevron = Chevron.For(file, x => x.IsExpanded);

        var count = new Border
        {
            Background = Palette.Hover,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = file.MatchCount.ToString(System.Globalization.CultureInfo.InvariantCulture), FontSize = Typography.CaptionSize, Foreground = Palette.TextSecondary },
        };

        // A DockPanel (not a StackPanel) so the folder text gets the remaining width and can trim.
        return new DockPanel
        {
            Margin = new Thickness(Spacing.S, 0, Spacing.M, 0),
            Background = Brushes.Transparent,
            Children =
            {
                count.Dock(Dock.Right),
                chevron.Dock(Dock.Left),
                FileIcons.File(file.FileName, margin: new Thickness(6, 0)).Dock(Dock.Left),
                new TextBlock { Text = file.FileName, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }.Dock(Dock.Left),
                new TextBlock
                {
                    Text = file.Folder,
                    FontSize = Typography.SmallSize,
                    Foreground = Palette.TextMuted,
                    Margin = new Thickness(6, 0, Spacing.S, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            },
        };
    }

    /// <summary>
    /// The line preview. While replace is open, the match is struck through and the replacement is
    /// shown right after it, so the outcome is visible before anything changes.
    /// </summary>
    private Control CreateMatchRow(SearchMatchRowViewModel match)
    {
        var matched = new Run(match.Matched) { Foreground = Palette.TextPrimary }
            .Bind(TextElement.BackgroundProperty, _search, x => x.IsReplaceVisible, replacing => replacing ? Palette.DiffRemoved : Palette.SearchMatchCurrent);
        matched.Bind(Inline.TextDecorationsProperty, _search, x => x.IsReplaceVisible, replacing => replacing ? TextDecorations.Strikethrough : null);
        var replacement = new Run { Foreground = Palette.TextPrimary, Background = Palette.DiffInserted }
            .Bind(Run.TextProperty, _search, x => x.ReplacementPreview);

        var text = new TextBlock
        {
            FontSize = Typography.SmallSize,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Inlines = new InlineCollection
            {
                new Run(match.Before) { Foreground = Palette.TextSecondary },
                matched,
                replacement,
                new Run(match.After) { Foreground = Palette.TextSecondary },
            },
        };
        ToolTip.SetTip(text, $"Line {match.LineLabel}");
        return new Border { Padding = new Thickness(MatchIndent, 0, Spacing.M, 0), Background = Brushes.Transparent, Child = text };
    }

    private void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (SearchOptionToggles.TryHandleKey(_search, e) || TryHandleReplaceAll(e))
        {
            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.Down && _search.Rows.Count > 0)
        {
            // Hand the keyboard to the results so arrows and Enter browse them.
            _search.SelectedRow ??= _search.Rows.FirstOrDefault(row => row is SearchMatchRowViewModel);
            _results.Focus();
            e.Handled = true;
        }
    }

    private void OnReplacementKeyDown(object? sender, KeyEventArgs e)
    {
        if (SearchOptionToggles.TryHandleKey(_search, e) || TryHandleReplaceAll(e))
        {
            e.Handled = true;
        }
    }

    /// <summary>Ctrl+Alt+Enter in either field replaces everything (after confirmation).</summary>
    private bool TryHandleReplaceAll(KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != (KeyModifiers.Control | KeyModifiers.Alt))
        {
            return false;
        }

        _ = _search.ReplaceAllAsync();
        return true;
    }

    private async void OnResultsKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key, _search.SelectedRow)
        {
            case (Key.Enter or Key.Space, { } row):
                e.Handled = true;
                await _search.ActivateAsync(row);
                break;
            case (Key.Left, SearchFileRowViewModel file):
                _search.SetExpanded(file, false);
                e.Handled = true;
                break;
            case (Key.Right, SearchFileRowViewModel file):
                _search.SetExpanded(file, true);
                e.Handled = true;
                break;
        }
    }
}
