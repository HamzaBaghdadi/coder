using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Coder.App.Controls;
using Coder.App.Mvvm;
using Coder.App.Theme;

namespace Coder.App.Workbench;

/// <summary>
/// The side bar on the right of the editors. It has no views yet; it is the place agents (and other
/// views moved out of the primary side bar) will live, so for now it explains itself calmly.
/// </summary>
public sealed class SecondarySideBar : Border
{
    public SecondarySideBar()
    {
        Background = Palette.SideBar;
        BorderBrush = Palette.Border;
        BorderThickness = new Thickness(1, 0, 0, 0);
        Child = new DockPanel
        {
            Children =
            {
                SideBarHeader.Create("SECONDARY SIDE BAR").Dock(Dock.Top),
                new StackPanel
                {
                    Spacing = Spacing.S,
                    Margin = new Thickness(Spacing.L, Spacing.XL),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children =
                    {
                        new Icon(Icons.LayoutSecondaryShown, 32) { Foreground = Palette.TextMuted, HorizontalAlignment = HorizontalAlignment.Center },
                        new TextBlock
                        {
                            Text = "Agents and other views will appear here.",
                            Foreground = Palette.TextSecondary,
                            TextWrapping = TextWrapping.Wrap,
                            TextAlignment = TextAlignment.Center,
                        },
                    },
                },
            },
        };
    }
}
