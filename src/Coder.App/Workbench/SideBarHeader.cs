using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Coder.App.Mvvm;
using Coder.App.Theme;

namespace Coder.App.Workbench;

/// <summary>The title row at the top of every side bar view ("Explorer", "Search") with its actions.</summary>
public static class SideBarHeader
{
    public static DockPanel Create(string title, params Control[] actions)
    {
        var actionPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Spacing.XXS };
        actionPanel.Children.AddRange(actions);

        return new DockPanel
        {
            Height = 36,
            Margin = new Thickness(Spacing.M, 0, Spacing.S, 0),
            Children =
            {
                actionPanel.Dock(Dock.Right),
                new TextBlock
                {
                    Text = title,
                    FontSize = Typography.UiSize,
                    FontWeight = Typography.Strong,
                    Foreground = Palette.TextPrimary,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
    }
}
