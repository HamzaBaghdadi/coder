using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Coder.App.Controls;
using Coder.App.Mvvm;
using Coder.App.Theme;
using Coder.Core.Git;
using Coder.Core.Syntax;
using Coder.Presentation.Mvvm;
using Coder.Presentation.SourceControl;

namespace Coder.App.Workbench;

/// <summary>
/// The Source Control side bar view: a commit message box with the Commit button, then the changes
/// grouped as merge conflicts, staged changes and changes. Each file row shows its git letter in the
/// change's color and, on hover, buttons to open, discard and stage or unstage it.
/// </summary>
public sealed class SourceControlView : DockPanel
{
    private readonly SourceControlViewModel _sourceControl;

    public SourceControlView(SourceControlViewModel sourceControl)
    {
        _sourceControl = sourceControl;
        Background = Palette.SideBar;

        var message = new TextBox
        {
            MinHeight = 30,
            MaxHeight = 140,
            Padding = new Thickness(8, 6),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalContentAlignment = VerticalAlignment.Top,
        }
        .Bind(TextBox.TextProperty, sourceControl, x => x.CommitMessage, Avalonia.Data.BindingMode.TwoWay)
        .Bind(TextBox.PlaceholderTextProperty, sourceControl, x => x.CommitPlaceholder);
        AutomationProperties.SetName(message, "Commit message");
        message.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Control)
            {
                _ = sourceControl.CommitAsync();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);

        var commit = new FlatButton
        {
            Classes = { "primary" },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            // Commit, Publish Branch or Sync Changes, whichever the repository needs next.
            Command = new RelayCommand(() => _ = sourceControl.RunPrimaryActionAsync()),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new Icon(Icons.Check, 15).Bind(Icon.DataProperty, sourceControl, x => x.PrimaryAction, action => action switch
                    {
                        SourceControlAction.Publish => Icons.Publish,
                        SourceControlAction.Sync => Icons.Sync,
                        _ => Icons.Check,
                    }),
                    new TextBlock { VerticalAlignment = VerticalAlignment.Center }.Bind(TextBlock.TextProperty, sourceControl, x => x.PrimaryActionText),
                },
            },
        }.Bind(IsEnabledProperty, sourceControl, x => x.IsBusy, busy => !busy);
        sourceControl.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SourceControlViewModel.PrimaryActionText) or "")
            {
                AutomationProperties.SetName(commit, sourceControl.PrimaryActionText);
            }
        };
        AutomationProperties.SetName(commit, sourceControl.PrimaryActionText);

        var error = new TextBlock { Foreground = Palette.Danger, TextWrapping = TextWrapping.Wrap, FontSize = Typography.SmallSize }
            .Bind(TextBlock.TextProperty, sourceControl, x => x.Message)
            .Bind(IsVisibleProperty, sourceControl, x => x.Message, text => text.Length > 0);

        var changes = new ListBox
        {
            ItemsSource = sourceControl.Rows,
            ItemContainerTheme = AppStyles.ListRowTheme,
            ItemTemplate = Templates.Row<SourceControlRow>(CreateRow),
        };
        AutomationProperties.SetName(changes, "Changes");

        // Screen readers read the list item, so the item (not the row content inside it) carries the name.
        changes.ContainerPrepared += (_, e) => AutomationProperties.SetName(e.Container, e.Container.DataContext switch
        {
            SourceControlFileRow file => $"{file.Change.RelativePath}, {file.Change.Kind}",
            SourceControlGroupRow group => $"{group.Title}, {group.Count}",
            _ => string.Empty,
        });
        ScrollViewer.SetHorizontalScrollBarVisibility(changes, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        // Clicking a file shows its changes; the row's Open File button opens the file itself.
        changes.Tapped += async (_, e) =>
        {
            if (e.Source is Visual { DataContext: SourceControlFileRow row } source && source.FindAncestorOfType<Button>() is null)
            {
                await sourceControl.OpenChangesAsync(row);
            }
        };
        changes.OnRowKeyDown(async (_, e) =>
        {
            if (e.Key == Key.Enter && changes.SelectedItem is SourceControlFileRow row)
            {
                e.Handled = true;
                await sourceControl.OpenChangesAsync(row);
            }
        });

        var repositoryContent = new DockPanel
        {
            Children =
            {
                new StackPanel
                {
                    Margin = new Thickness(Spacing.M, 0, Spacing.M, Spacing.S),
                    Spacing = Spacing.S,
                    Children = { message, commit, error },
                }.Dock(Dock.Top),
                ChangesAndHistory(changes, new HistoryView(sourceControl.History), sourceControl.History),
            },
        }.Bind(IsVisibleProperty, sourceControl, x => x.HasRepository);

        var noRepository = new TextBlock
        {
            Margin = new Thickness(Spacing.L, Spacing.S),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Palette.TextSecondary,
            LineHeight = 20,
            Text = GitRepository.IsGitInstalled
                ? "The open folder is not in a git repository. Open a folder inside one to see its changes here."
                : "Git was not found. Install git and restart Coder to use Source Control.",
        }.Bind(IsVisibleProperty, sourceControl, x => x.HasRepository, has => !has);

        Children.Add(SideBarHeader.Create("SOURCE CONTROL",
            new FlatButton(Icons.Check, "Commit (Ctrl+Enter)") { Command = new RelayCommand(() => _ = sourceControl.CommitAsync()) },
            new FlatButton(Icons.ArrowDown, "Pull") { Command = new RelayCommand(() => _ = sourceControl.PullAsync()) },
            new FlatButton(Icons.ArrowUp, "Push") { Command = new RelayCommand(() => _ = sourceControl.PushAsync()) },
            new FlatButton(Icons.Refresh, "Refresh") { Command = new RelayCommand(() => _ = sourceControl.RefreshAsync()) }).Dock(Dock.Top));
        Children.Add(new Panel { Children = { repositoryContent, noRepository } });
    }

    /// <summary>
    /// The changes above the GRAPH section, sharing the height (a splitter moves the line between them).
    /// A collapsed graph shrinks to its header at the bottom.
    /// </summary>
    private static Grid ChangesAndHistory(Control changes, HistoryView history, HistoryViewModel model)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto,*") };
        var splitter = new GridSplitter
        {
            Height = 4,
            Background = Brushes.Transparent,
            ResizeDirection = GridResizeDirection.Rows,
        }.Bind(IsVisibleProperty, model, x => x.IsExpanded);
        Grid.SetRow(changes, 0);
        Grid.SetRow(splitter, 1);
        Grid.SetRow(history, 2);
        grid.Children.AddRange([changes, splitter, history]);

        void Follow()
        {
            // Back to sharing the height evenly: a dragged splitter leaves pixel heights behind.
            grid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            grid.RowDefinitions[2].Height = model.IsExpanded ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        }

        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HistoryViewModel.IsExpanded))
            {
                Follow();
            }
        };
        Follow();
        return grid;
    }

    private Control CreateRow(SourceControlRow row) => row switch
    {
        SourceControlGroupRow group => CreateGroupRow(group),
        SourceControlFileRow file => CreateFileRow(file),
        _ => new TextBlock(),
    };

    private Control CreateGroupRow(SourceControlGroupRow group)
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        if (group.Group == SourceControlGroup.Staged)
        {
            actions.Children.Add(SmallButton(Icons.Minus, "Unstage All Changes", () => _sourceControl.UnstageAllAsync()));
        }
        else if (group.Count > 0)
        {
            if (group.Group == SourceControlGroup.Changes)
            {
                actions.Children.Add(SmallButton(Icons.Discard, "Discard All Changes", () => _sourceControl.DiscardAllAsync()));
            }

            actions.Children.Add(SmallButton(Icons.Plus, "Stage All Changes", () => _sourceControl.StageAllAsync()));
        }

        return new DockPanel
        {
            Margin = new Thickness(Spacing.M, 0, Spacing.S, 0),
            Background = Brushes.Transparent,
            Children =
            {
                CountBadge(group.Count).Dock(Dock.Right),
                actions.Dock(Dock.Right),
                new TextBlock
                {
                    Text = group.Title.ToUpperInvariant(),
                    FontSize = Typography.CaptionSize,
                    FontWeight = FontWeight.SemiBold,
                    LetterSpacing = 0.4,
                    Foreground = Palette.TextSecondary,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
    }

    private Control CreateFileRow(SourceControlFileRow row)
    {
        var change = row.Change;
        var color = GitColors.For(change.Kind);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Opacity = 0 };
        if (change.Kind != GitChangeKind.Deleted)
        {
            actions.Children.Add(SmallButton(Icons.GoToFile, $"Open {change.FileName}", () => _sourceControl.OpenAsync(row)));
        }

        if (row.Group == SourceControlGroup.Staged)
        {
            actions.Children.Add(SmallButton(Icons.Minus, $"Unstage {change.FileName}", () => _sourceControl.UnstageAsync(row)));
        }
        else
        {
            if (row.Group == SourceControlGroup.Changes)
            {
                actions.Children.Add(SmallButton(Icons.Discard, $"Discard Changes in {change.FileName}", () => _sourceControl.DiscardAsync(row)));
            }

            actions.Children.Add(SmallButton(Icons.Plus, $"Stage {change.FileName}", () => _sourceControl.StageAsync(row)));
        }

        var name = new TextBlock
        {
            Text = change.FileName,
            Foreground = color,
            VerticalAlignment = VerticalAlignment.Center,
            TextDecorations = change.Kind == GitChangeKind.Deleted ? TextDecorations.Strikethrough : null,
        };
        var folder = new TextBlock
        {
            Text = row.Folder,
            FontSize = Typography.SmallSize,
            Foreground = Palette.TextMuted,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.LeadingCharacterEllipsis,
        };

        var panel = new DockPanel
        {
            Margin = new Thickness(Spacing.L + Spacing.XS, 0, Spacing.S, 0),
            Background = Brushes.Transparent,
            Children =
            {
                new TextBlock
                {
                    Text = change.Letter,
                    Foreground = color,
                    FontWeight = FontWeight.SemiBold,
                    Width = 16,
                    TextAlignment = TextAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                }.Dock(Dock.Right),
                actions.Dock(Dock.Right),
                FileIcons.File(change.Path, margin: new Thickness(0, 0, 6, 0)).Dock(Dock.Left),
                name.Dock(Dock.Left),
                new Border { Width = 6 }.Dock(Dock.Left),
                folder,
            },
        };

        // Row actions appear on hover, like VS Code, so the list stays calm. They are transparent rather
        // than hidden, so keyboard focus and screen readers still reach them.
        panel.PointerEntered += (_, _) => actions.Opacity = 1;
        panel.PointerExited += (_, _) => actions.Opacity = actions.IsKeyboardFocusWithin ? 1 : 0;
        actions.GotFocus += (_, _) => actions.Opacity = 1;
        actions.LostFocus += (_, _) => actions.Opacity = panel.IsPointerOver ? 1 : 0;
        ToolTip.SetTip(panel, change.OriginalPath is null ? change.RelativePath : $"{change.RelativePath} (renamed from {Path.GetFileName(change.OriginalPath)})");
        return panel;
    }

    private static FlatButton SmallButton(Avalonia.Media.Geometry icon, string name, Func<Task> action) =>
        new(icon, name, 14) { Width = 22, Height = 22, Command = new RelayCommand(() => _ = action()) };

    private static Border CountBadge(int count) => new()
    {
        MinWidth = 18,
        Height = 18,
        Padding = new Thickness(5, 0),
        Margin = new Thickness(4, 0, 0, 0),
        CornerRadius = new CornerRadius(9),
        Background = Palette.Pressed,
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            FontSize = Typography.CaptionSize,
            Foreground = Palette.TextSecondary,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };
}
