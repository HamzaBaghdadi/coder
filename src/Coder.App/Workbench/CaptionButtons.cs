using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia.Layout;
using Coder.App.Controls;
using Coder.App.Theme;

namespace Coder.App.Workbench;

/// <summary>
/// Minimize / maximize / close, drawn by the app so they match the theme. Each button carries its
/// platform role, so the OS still treats them as caption buttons (e.g. Windows 11 snap layouts).
/// </summary>
public sealed class CaptionButtons : StackPanel
{
    private const double ButtonWidth = 46;

    // Fully qualified: inside a StackPanel, "Spacing" and "Theme" resolve to its own properties.
    private const double ButtonHeight = Coder.App.Theme.Spacing.TitleBarHeight;

    public CaptionButtons(Window window)
    {
        Orientation = Orientation.Horizontal;
        HorizontalAlignment = HorizontalAlignment.Right;

        var maximizeIcon = new Icon(Icons.Maximize, 16) { StrokeThickness = 1.4 };
        var maximize = Button(maximizeIcon, "Maximize", WindowDecorationsElementRole.MaximizeButton, () =>
            window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);

        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty)
            {
                var maximized = window.WindowState == WindowState.Maximized;
                maximizeIcon.Data = maximized ? Icons.Restore : Icons.Maximize;
                Avalonia.Automation.AutomationProperties.SetName(maximize, maximized ? "Restore" : "Maximize");
            }
        };

        Children.Add(Button(new Icon(Icons.Minimize, 16) { StrokeThickness = 1.4 }, "Minimize", WindowDecorationsElementRole.MinimizeButton,
            () => window.WindowState = WindowState.Minimized));
        Children.Add(maximize);
        Children.Add(Button(new Icon(Icons.Close, 16) { StrokeThickness = 1.4 }, "Close", WindowDecorationsElementRole.CloseButton, window.Close, "close"));
    }

    private static FlatButton Button(Icon icon, string name, WindowDecorationsElementRole role, Action action, string? extraClass = null)
    {
        var button = new FlatButton
        {
            Content = icon,
            Width = ButtonWidth,
            Height = ButtonHeight,
            Focusable = false, // Like native caption buttons: mouse only, the window has Alt+F4.
        };
        button.Classes.Add("caption");
        if (extraClass is not null)
        {
            button.Classes.Add(extraClass);
        }

        button.Click += (_, _) => action();
        Avalonia.Automation.AutomationProperties.SetName(button, name);
        WindowDecorationProperties.SetElementRole(button, role);
        return button;
    }
}
