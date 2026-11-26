using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Coder.App.Mvvm;
using Coder.App.Theme;

namespace Coder.App.Workbench;

/// <summary>The title row at the top of every side bar view ("EXPLORER", "SEARCH") with its actions.</summary>
public static class SideBarHeader
{
    public static DockPanel Create(string title, params Control[] actions)
    {
        var actionPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        actionPanel.Children.AddRange(actions);

        return new DockPanel
        {
            Height = 36,
            Margin = new Thickness(Spacing.L, 0, Spacing.S, 0),
            Children =
            {
                actionPanel.Dock(Dock.Right),
                new TextBlock
                {
                    Text = title,
                    FontSize = Typography.CaptionSize,
                    FontWeight = FontWeight.SemiBold,
                    LetterSpacing = 0.6,
                    Foreground = Palette.TextSecondary,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
    }
}
