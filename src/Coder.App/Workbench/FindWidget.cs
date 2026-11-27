using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Coder.App.Controls;
using Coder.App.Mvvm;
using Coder.App.Theme;
using Coder.Presentation.Editors;
using Coder.Presentation.Mvvm;

namespace Coder.App.Workbench;

/// <summary>
/// The find/replace card floating over the editor's top-right corner.
/// Keys: Enter / Shift+Enter next / previous, Esc close, Alt+C / Alt+W / Alt+R toggle options,
/// Enter in the replace field replaces one, Ctrl+Alt+Enter replaces all, Alt+Enter puts a cursor on
/// every match.
/// </summary>
public sealed class FindWidget : Border
{
    private const double FieldWidth = 250;

    private readonly FindViewModel _find;
    private readonly TextBox _query;
    private readonly TextBox _replacement;

    public FindWidget(FindViewModel find)
    {
        _find = find;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(0, Spacing.S, 22, 0);
        Padding = new Thickness(Spacing.XS, Spacing.S, Spacing.S, Spacing.S);
        Background = Palette.Elevated;
        BorderBrush = Palette.BorderStrong;
        BorderThickness = new Thickness(1);
        CornerRadius = Spacing.RadiusLarge;
        ThemeManager.BindShadow(this, "0 8 24 0");
        this.Bind(IsVisibleProperty, find, x => x.IsOpen);

        _query = Field("Find", x => x.Query);
        _replacement = Field("Replace", x => x.Replacement);
        _query.InnerRightContent = SearchOptionToggles.Create(find);

        var status = new TextBlock
        {
            Width = 74,
            FontSize = Typography.SmallSize,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(Spacing.S, 0, Spacing.XS, 0),
        }
        .Bind(TextBlock.TextProperty, find, x => x.StatusText)
        .Bind(TextBlock.ForegroundProperty, find, x => x.HasError, hasError => hasError ? Palette.Danger : Palette.TextSecondary);
        ToolTip.SetTip(status, "Matches");

        var expander = new FlatButton(Icons.ChevronRight, "Toggle Replace (Ctrl+H)", iconSize: 14)
        {
            Width = 20,
            Height = double.NaN, // spans both rows (find and replace)
            VerticalAlignment = VerticalAlignment.Stretch,
            Padding = new Thickness(0),
            Command = new RelayCommand(() => find.IsReplaceVisible = !find.IsReplaceVisible),
        };
        var expanderIcon = (Icon)expander.Content!;
        void UpdateExpander() => expanderIcon.RenderTransform = new RotateTransform(find.IsReplaceVisible ? 90 : 0);
        UpdateExpander();

        var findRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Spacing.XXS,
            Children =
            {
                _query,
                status,
                Action(Icons.ArrowUp, "Previous Match (Shift+Enter)", find.FindPrevious),
                Action(Icons.ArrowDown, "Next Match (Enter)", find.FindNext),
                Action(Icons.Close, "Close (Escape)", find.Close),
            },
        };

        var replaceRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Spacing.XXS,
            Margin = new Thickness(0, Spacing.XS, 0, 0),
            Children =
            {
                _replacement,
                new Border { Width = Spacing.S },
                Action(Icons.Replace, "Replace (Enter)", find.ReplaceCurrent),
                Action(Icons.ReplaceAll, "Replace All (Ctrl+Alt+Enter)", find.ReplaceAll),
            },
        }.Bind(IsVisibleProperty, find, x => x.IsReplaceVisible);

        Child = new DockPanel
        {
            Children =
            {
                expander.Dock(Dock.Left),
                new StackPanel { Margin = new Thickness(Spacing.XS, 0, 0, 0), Children = { findRow, replaceRow } },
            },
        };

        _query.AddHandler(KeyDownEvent, (_, e) => OnFieldKeyDown(e, isReplaceField: false), RoutingStrategies.Tunnel);
        _replacement.AddHandler(KeyDownEvent, (_, e) => OnFieldKeyDown(e, isReplaceField: true), RoutingStrategies.Tunnel);

        find.FocusRequested += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            _query.Focus();
            _query.SelectAll();
        });
        find.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FindViewModel.IsReplaceVisible))
            {
                UpdateExpander();
            }
        };
    }

    private TextBox Field(string placeholder, System.Linq.Expressions.Expression<Func<FindViewModel, string>> path)
    {
        var box = new TextBox
        {
            Width = FieldWidth,
            Height = 28,
            MinHeight = 28,
            Padding = new Thickness(Spacing.S, 0),
            PlaceholderText = placeholder,
            FontFamily = Typography.Code,
            FontSize = Typography.UiSize,
            VerticalContentAlignment = VerticalAlignment.Center,
        }.Bind(TextBox.TextProperty, _find, path, Avalonia.Data.BindingMode.TwoWay);
        AutomationProperties.SetName(box, placeholder);
        return box;
    }

    private static FlatButton Action(Geometry icon, string name, Action action) => new(icon, name)
    {
        Width = 26,
        Height = 26,
        Command = new RelayCommand(action),
    };

    private void OnFieldKeyDown(KeyEventArgs e, bool isReplaceField)
    {
        if (SearchOptionToggles.TryHandleKey(_find, e))
        {
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                _find.Close();
                break;
            case Key.Enter when e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Alt):
                _find.ReplaceAll();
                break;
            case Key.Enter when e.KeyModifiers == KeyModifiers.Alt:
                _find.SelectAllMatches();
                break;
            case Key.Enter when isReplaceField:
                _find.ReplaceCurrent();
                break;
            case Key.Enter when e.KeyModifiers == KeyModifiers.Shift:
                _find.FindPrevious();
                break;
            case Key.Enter:
                _find.FindNext();
                break;
            default:
                return;
        }

        e.Handled = true;
    }
}
