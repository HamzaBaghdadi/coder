using System.ComponentModel;
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

namespace Coder.App.Workbench;

/// <summary>
/// An extension's page in the editor area: a header (icon, name, publisher and version, description, the
/// same actions as its row in the Extensions view) above its README, rendered like the Markdown preview.
/// </summary>
public sealed class ExtensionDetailsView : DockPanel
{
    private const double IconSize = 72;

    private readonly Border _header = new();
    private readonly MarkdownDocumentView _readme = new() { CenterColumn = false };
    private readonly TextBlock _status = new() { Classes = { "secondary" }, Margin = new Thickness(Spacing.XXL, Spacing.XL) };
    private ExtensionDetailsTabViewModel? _tab;

    public ExtensionDetailsView(Func<string, Task> openFile)
    {
        _readme.OpenFile = openFile;
        _header.BorderBrush = Palette.Border;
        _header.BorderThickness = new Thickness(0, 0, 0, 1);
        Children.Add(_header.Dock(Dock.Top));
        Children.Add(new Panel { Children = { _readme, _status } });
        AutomationProperties.SetName(this, "Extension Details");
    }

    /// <summary>The README view (tests).</summary>
    internal MarkdownDocumentView Readme => _readme;

    public ExtensionDetailsTabViewModel? Tab
    {
        get => _tab;
        set
        {
            if (ReferenceEquals(_tab, value))
            {
                return;
            }

            if (_tab is not null)
            {
                _tab.PropertyChanged -= OnTabChanged;
            }

            _tab = value;
            if (_tab is not null)
            {
                _tab.PropertyChanged += OnTabChanged;
            }

            BuildHeader();
            ShowReadme();
        }
    }

    private void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ExtensionDetailsTabViewModel.Readme) or nameof(ExtensionDetailsTabViewModel.IsLoading))
        {
            ShowReadme();
        }
        else if (e.PropertyName == nameof(ExtensionDetailsTabViewModel.Item))
        {
            BuildHeader(); // installed, uninstalled, busy
        }
    }

    private void ShowReadme()
    {
        var tab = _tab;
        var readme = tab?.Readme;
        _readme.IsVisible = readme is not null;
        _status.IsVisible = readme is null;
        _status.Text = tab is null ? string.Empty : tab.IsLoading ? "Loading the README…" : "This extension has no README.";
        if (readme is not null)
        {
            _readme.Show(readme, tab!.BaseFolder);
        }
    }

    private void BuildHeader()
    {
        if (_tab?.Item is not { } item)
        {
            _header.Child = null;
            return;
        }

        var icon = new Border
        {
            Width = IconSize,
            Height = IconSize,
            CornerRadius = Spacing.RadiusLarge,
            ClipToBounds = true,
            Background = Palette.Hover,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new Icon(Icons.Extensions, 36) { Foreground = Palette.TextMuted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        if ((item.IconPath ?? item.IconUrl) is { } address)
        {
            _ = LoadIconAsync(icon, address);
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Spacing.S, Margin = new Thickness(0, Spacing.S, 0, 0) };
        if (item.IsInstalled)
        {
            if (item.HasThemes)
            {
                actions.Children.Add(ExtensionsView.Action("Set Theme", $"Set Theme: {item.DisplayName}", item.SetTheme, primary: true));
            }

            actions.Children.Add(ExtensionsView.Action("Uninstall", $"Uninstall {item.DisplayName}", item.Uninstall, primary: false));
        }
        else
        {
            actions.Children.Add(ExtensionsView.Action("Install", $"Install {item.DisplayName}", item.Install, primary: true)
                .Bind(IsEnabledProperty, item, x => x.IsBusy, busy => !busy));
            actions.Children.Add(new TextBlock { Classes = { "secondary" }, FontSize = Typography.SmallSize, VerticalAlignment = VerticalAlignment.Center }
                .Bind(TextBlock.TextProperty, item, x => x.Status));
        }

        _header.Padding = new Thickness(Spacing.XXL, Spacing.XL);
        _header.Child = new DockPanel
        {
            MaxWidth = 860 + 2 * Spacing.XXL,
            HorizontalAlignment = HorizontalAlignment.Left,
            Children =
            {
                icon.Dock(Dock.Left),
                new StackPanel
                {
                    Margin = new Thickness(Spacing.L, 0, 0, 0),
                    Spacing = Spacing.XS,
                    Children =
                    {
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = Spacing.S,
                            Children =
                            {
                                new TextBlock { Text = item.DisplayName, Classes = { "heading" } },
                                new Border
                                {
                                    Classes = { "kbd" },
                                    VerticalAlignment = VerticalAlignment.Center,
                                    Child = new TextBlock { Text = $"v{item.Version}", FontSize = Typography.CaptionSize, Foreground = Palette.TextSecondary },
                                },
                            },
                        },
                        new TextBlock { Text = item.Details, Classes = { "secondary" }, FontSize = Typography.SmallSize, TextTrimming = TextTrimming.CharacterEllipsis },
                        new TextBlock { Text = item.Description, TextWrapping = TextWrapping.Wrap, IsVisible = item.Description.Length > 0 },
                        actions,
                    },
                },
            },
        };
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
