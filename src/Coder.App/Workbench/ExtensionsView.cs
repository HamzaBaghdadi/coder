using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Coder.App.Controls;
using Coder.App.Mvvm;
using Coder.App.Services;
using Coder.App.Theme;
using Coder.Presentation.Extensions;
using Coder.Presentation.Mvvm;

namespace Coder.App.Workbench;

/// <summary>
/// The Extensions view (Ctrl+Shift+X): a search box, the installed extensions, then what the
/// marketplace offers. Each row shows the extension's icon, name, publisher and installs, and its
/// actions: Install, or Set Theme and Uninstall once installed.
/// </summary>
public sealed class ExtensionsView : DockPanel
{
    private const double IconSize = 36;

    private readonly ExtensionsViewModel _extensions;

    public ExtensionsView(ExtensionsViewModel extensions)
    {
        _extensions = extensions;
        Background = Palette.SideBar;

        var search = new TextBox
        {
            Height = 28,
            MinHeight = 28,
            Padding = new Thickness(Spacing.S, Spacing.XS),
            PlaceholderText = "Search themes in the marketplace",
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(Spacing.M, 0, Spacing.M, Spacing.S),
        }.Bind(TextBox.TextProperty, extensions, x => x.Query, Avalonia.Data.BindingMode.TwoWay);
        AutomationProperties.SetName(search, "Search extensions");

        var refresh = new FlatButton(Icons.Refresh, "Refresh Extensions") { Command = new RelayCommand(() => _ = extensions.SearchAsync()) };
        Children.Add(SideBarHeader.Create("Extensions", refresh).Dock(Dock.Top));
        Children.Add(search.Dock(Dock.Top));

        var message = new TextBlock
        {
            Foreground = Palette.TextSecondary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(Spacing.M, Spacing.S),
            FontSize = Typography.SmallSize,
        }.Bind(TextBlock.TextProperty, extensions, x => x.Message)
         .Bind(IsVisibleProperty, extensions, x => x.Message, text => text.Length > 0);

        var searching = new TextBlock
        {
            Text = "Searching…",
            Foreground = Palette.TextSecondary,
            FontSize = Typography.SmallSize,
            Margin = new Thickness(Spacing.M, Spacing.XS),
        }.Bind(IsVisibleProperty, extensions, x => x.IsSearching);

        var content = new StackPanel
        {
            Children =
            {
                Section("Installed", extensions.Installed),
                List(extensions.Installed),
                Section(extensions.SourcesLabel, extensions.Results).Bind(IsVisibleProperty, extensions, x => x.HasSources),
                searching,
                message,
                List(extensions.Results),
                new TextBlock
                {
                    Text = "Extensions add color themes for now. Coder runs nothing from them: they are data only.",
                    Foreground = Palette.TextMuted,
                    FontSize = Typography.CaptionSize,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(Spacing.M, Spacing.L, Spacing.M, Spacing.M),
                },
            },
        };

        Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = content,
        });
    }

    /// <summary>A section title with the number of items in a pill beside it.</summary>
    private static Control Section(string title, System.Collections.ObjectModel.ObservableCollection<ExtensionItemViewModel> items)
    {
        const double BadgeHeight = 18;
        var count = new TextBlock
        {
            FontSize = Typography.CaptionSize,
            Foreground = Palette.TextSecondary,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        void Update()
        {
            count.Text = items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        Update();
        items.CollectionChanged += (_, _) => Update();
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Spacing.S,
            Margin = new Thickness(Spacing.M, Spacing.M, Spacing.M, Spacing.XS),
            Children =
            {
                new TextBlock { Classes = { "section-title" }, Text = title, VerticalAlignment = VerticalAlignment.Center },
                new Border
                {
                    MinWidth = BadgeHeight,
                    Height = BadgeHeight,
                    Padding = new Thickness(5, 0),
                    CornerRadius = new CornerRadius(BadgeHeight / 2),
                    Background = Palette.Pressed,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = count,
                },
            },
        };
    }

    private static ItemsControl List(System.Collections.ObjectModel.ObservableCollection<ExtensionItemViewModel> items) => new()
    {
        ItemsSource = items,
        ItemTemplate = Templates.Row<ExtensionItemViewModel>(Row),
    };

    private static Control Row(ExtensionItemViewModel item)
    {
        var icon = new Border
        {
            Width = IconSize,
            Height = IconSize,
            CornerRadius = Spacing.RadiusMedium,
            ClipToBounds = true,
            Background = Palette.Hover,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new Icon(Icons.Extensions, 20) { Foreground = Palette.TextMuted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        if ((item.IconPath ?? item.IconUrl) is { } address)
        {
            _ = LoadIconAsync(icon, address);
        }

        var name = new TextBlock { Text = item.DisplayName, FontWeight = Typography.Strong, TextTrimming = TextTrimming.CharacterEllipsis };
        var version = new TextBlock { Text = item.Version, Foreground = Palette.TextSecondary, FontSize = Typography.CaptionSize, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        var description = new TextBlock
        {
            Text = item.Description,
            Foreground = Palette.TextSecondary,
            FontSize = Typography.SmallSize,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 2,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = item.Description.Length > 0,
        };
        var details = new TextBlock { Text = item.Details, Foreground = Palette.TextSecondary, FontSize = Typography.CaptionSize, TextTrimming = TextTrimming.CharacterEllipsis };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Spacing.XS, Margin = new Thickness(0, Spacing.XS, 0, 0) };
        if (item.IsInstalled)
        {
            if (item.HasThemes)
            {
                actions.Children.Add(Action("Set Theme", $"Set Theme: {item.DisplayName}", item.SetTheme, primary: true));
            }

            actions.Children.Add(Action("Uninstall", $"Uninstall {item.DisplayName}", item.Uninstall, primary: false));
        }
        else
        {
            var install = Action("Install", $"Install {item.DisplayName}", item.Install, primary: true)
                .Bind(IsEnabledProperty, item, x => x.IsBusy, busy => !busy);
            actions.Children.Add(install);
            actions.Children.Add(new TextBlock { Foreground = Palette.TextSecondary, FontSize = Typography.SmallSize, VerticalAlignment = VerticalAlignment.Center }
                .Bind(TextBlock.TextProperty, item, x => x.Status));
        }

        // A rounded row inset from the edges, like the list rows elsewhere.
        var row = new Border
        {
            Margin = new Thickness(Spacing.XS, 0),
            Padding = new Thickness(Spacing.S),
            CornerRadius = Spacing.RadiusMedium,
            Background = Brushes.Transparent,
            Child = new DockPanel
            {
                Children =
                {
                    icon.Dock(Dock.Left),
                    new StackPanel
                    {
                        Margin = new Thickness(Spacing.M, 0, 0, 0),
                        Spacing = 2,
                        Children =
                        {
                            new DockPanel { Children = { version.Dock(Dock.Right), name } },
                            description,
                            details,
                            actions,
                        },
                    },
                },
            },
        };
        row.PointerEntered += (_, _) => row.Background = Palette.Hover;
        row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
        AutomationProperties.SetName(row, item.DisplayName);
        ToolTip.SetTip(row, item.Id);
        return row;
    }

    private static FlatButton Action(string text, string name, System.Windows.Input.ICommand command, bool primary)
    {
        var button = new FlatButton
        {
            Content = text,
            Command = command,
            Padding = new Thickness(10, 2),
            MinHeight = 24,
            FontSize = Typography.SmallSize,
        };
        button.Classes.Add(primary ? "primary" : "secondary");

        AutomationProperties.SetName(button, name);
        return button;
    }

    private static async Task LoadIconAsync(Border frame, string address)
    {
        if (await ExtensionIcons.LoadAsync(address) is { } bitmap)
        {
            frame.Background = Brushes.Transparent;
            frame.Child = new Image { Source = bitmap, Width = IconSize, Height = IconSize };
        }
    }
}
