using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Coder.App.Controls;
using Coder.App.Mvvm;
using Coder.App.Theme;
using Coder.Presentation.Mvvm;
using Coder.Presentation.Settings;

namespace Coder.App.Workbench;

/// <summary>
/// The settings page: a search box and every setting, grouped, each with a plain sentence saying what it
/// does and the control to change it. Changes apply as you make them.
/// </summary>
public sealed class SettingsView : DockPanel
{
    private const double PageWidth = 760;

    private readonly TextBox _search;
    private readonly StackPanel _list = new() { Spacing = Spacing.XS };
    private SettingsPageViewModel? _page;

    public SettingsView()
    {
        Background = Palette.EditorBackground;
        _search = new TextBox { PlaceholderText = "Search settings", Height = 32, VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(_search, "Search settings");
        _search.TextChanged += (_, _) => _page?.Query = _search.Text ?? string.Empty;

        var json = new FlatButton { Content = "Edit in settings.json", Classes = { "secondary" }, VerticalAlignment = VerticalAlignment.Center };
        json.Click += async (_, _) =>
        {
            if (_page?.OpenJson is { } open)
            {
                await open();
            }
        };

        var header = new DockPanel
        {
            Margin = new Thickness(0, Spacing.XXL, 0, Spacing.L),
            Children =
            {
                json.Dock(Dock.Right),
                new TextBlock { Text = "Settings", Classes = { "heading" }, VerticalAlignment = VerticalAlignment.Center },
            },
        };

        Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new StackPanel
            {
                MaxWidth = PageWidth,
                Margin = new Thickness(Spacing.XL, 0, Spacing.XL, Spacing.XL),
                Children = { header, _search, new Border { Height = Spacing.L }, _list },
            },
        });
    }

    public SettingsPageViewModel? Page
    {
        get => _page;
        set
        {
            if (ReferenceEquals(_page, value))
            {
                return;
            }

            if (_page is not null)
            {
                _page.Visible.CollectionChanged -= OnVisibleChanged;
            }

            _page = value;
            if (_page is not null)
            {
                _page.Visible.CollectionChanged += OnVisibleChanged;
                _search.Text = _page.Query;
            }

            Rebuild();
        }
    }

    public void FocusSearch() => _search.Focus();

    private void OnVisibleChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        _list.Children.Clear();
        if (_page is null)
        {
            return;
        }

        string? category = null;
        foreach (var item in _page.Visible)
        {
            if (item.Category != category)
            {
                category = item.Category;
                _list.Children.Add(new TextBlock
                {
                    Text = category,
                    Classes = { "title" },
                    Margin = new Thickness(Spacing.M, _list.Children.Count == 0 ? Spacing.XS : Spacing.XL, 0, Spacing.XS),
                });
            }

            _list.Children.Add(Row(item));
        }

        if (_page.Visible.Count == 0)
        {
            _list.Children.Add(new TextBlock { Text = "No settings match. Try another word.", Classes = { "secondary" }, Margin = new Thickness(Spacing.M) });
        }
    }

    /// <summary>Title and description on the left, the control on the right; the row lights up on hover.</summary>
    private static Control Row(SettingItem item)
    {
        var text = new StackPanel
        {
            Spacing = Spacing.XXS,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = item.Title, FontWeight = Typography.Strong, Foreground = Palette.TextPrimary, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = item.Description, Classes = { "secondary" }, TextWrapping = TextWrapping.Wrap, FontSize = Typography.SmallSize, LineHeight = 18 },
            },
        };
        if (item.Note is { } note)
        {
            text.Children.Add(new TextBlock { Text = note, Foreground = Palette.TextMuted, FontSize = Typography.CaptionSize });
        }

        if (item is ActionSetting action)
        {
            text.Children.Add(new TextBlock { Foreground = Palette.TextMuted, FontSize = Typography.CaptionSize, TextWrapping = TextWrapping.Wrap }
                .Bind(TextBlock.TextProperty, action, x => x.Status));
        }

        var control = Editor(item);
        AutomationProperties.SetName(control, item.Title);
        control.VerticalAlignment = VerticalAlignment.Center;
        control.Margin = new Thickness(Spacing.L, 0, 0, 0);

        var row = new Border
        {
            Padding = new Thickness(Spacing.M, Spacing.M),
            CornerRadius = Spacing.RadiusMedium,
            Background = Brushes.Transparent,
            Child = new DockPanel { Children = { control.Dock(Dock.Right), text } },
        };
        row.PointerEntered += (_, _) => row.Background = Palette.Hover;
        row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
        return row;
    }

    private static Control Editor(SettingItem item) => item switch
    {
        ToggleSetting toggle => new ToggleSwitch { OnContent = null, OffContent = null, MinWidth = 0 }
            .Bind(ToggleSwitch.IsCheckedProperty, toggle, x => x.Value, Avalonia.Data.BindingMode.TwoWay),
        NumberSetting number => new NumberBox { Minimum = number.Minimum, Maximum = number.Maximum, Width = 130 }
            .Bind(NumberBox.ValueProperty, number, x => x.Value, Avalonia.Data.BindingMode.TwoWay),
        ChoiceSetting choice => new ComboBox { ItemsSource = choice.Options, MinWidth = 200 }
            .Bind(ComboBox.SelectedItemProperty, choice, x => x.Selected, Avalonia.Data.BindingMode.TwoWay),
        ActionSetting action => ActionButton(action),
        _ => new Panel(),
    };

    private static FlatButton ActionButton(ActionSetting action)
    {
        var button = new FlatButton { Classes = { "secondary" }, MinWidth = 130, HorizontalContentAlignment = HorizontalAlignment.Center }
            .Bind(ContentControl.ContentProperty, action, x => x.ActionLabel);
        button.Click += async (_, _) => await action.RunAsync();
        return button;
    }
}
