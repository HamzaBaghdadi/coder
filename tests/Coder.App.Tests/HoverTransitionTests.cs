using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Coder.App.Controls;
using Coder.App.Theme;

namespace Coder.App.Tests;

public sealed class HoverTransitionTests
{
    /// <summary>
    /// Regression: the hover fade used to start from transparent white, so its in-between colors were a
    /// light gray (e.g. #0EF9F9F9) before the dark hover color arrived. The fade must change opacity only:
    /// in every state, the background keeps the hover color's RGB.
    /// </summary>
    [AvaloniaFact]
    public void Hover_fade_only_changes_opacity_never_color()
    {
        var button = new FlatButton { Width = 200, Height = 40, Content = "Open Folder", HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var window = new Window { Width = 300, Height = 100, Content = button, Background = Palette.EditorBackground };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var hover = ((ISolidColorBrush)Palette.Hover).Color;

        AssertSameRgb(hover, button.Background);

        window.MouseMove(new Point(150, 20));          // fade in
        Assert.True(button.IsPointerOver);
        AssertSameRgb(hover, button.Background);

        window.MouseMove(new Point(280, 80));          // fade out
        AssertSameRgb(hover, button.Background);
    }

    private static void AssertSameRgb(Color expected, IBrush? actual)
    {
        var color = Assert.IsAssignableFrom<ISolidColorBrush>(actual).Color;
        Assert.Equal((expected.R, expected.G, expected.B), (color.R, color.G, color.B));
    }
}
