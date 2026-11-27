using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Coder.App.Services;
using Coder.App.Theme;

namespace Coder.App.Tests;

public sealed class MessageDialogTests
{
    /// <summary>Regression: the primary button once had a local null foreground, which hid its label.</summary>
    [AvaloniaFact]
    public void Every_dialog_button_shows_its_label()
    {
        var window = new Window { Width = 800, Height = 500 };
        window.Show();
        try
        {
            var dialog = new MessageDialog<bool>(
                "Discard all 2 changes?", "This can't be undone.",
                [new("Cancel", false), new("Discard All", true, IsPrimary: true)], cancelResult: false);
            _ = dialog.ShowAsync(window);

            var buttons = dialog.Buttons.ToList();
            Assert.Equal(2, buttons.Count);

            var primary = Assert.Single(buttons, b => b.Classes.Contains("primary"));
            Assert.Equal("Discard All", primary.Content);
            Assert.Same(Palette.TextOnAccent, primary.Foreground);
            Assert.All(buttons, b => Assert.NotNull(b.Foreground));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Saves a capture to artifacts/screenshots; the repository is found from this source file, since
    /// builds may run from an artifacts folder outside it.</summary>
    private static void SaveScreenshot(Window window, string name, [System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "")
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var root = Path.GetDirectoryName(sourceFile)!;
        while (!File.Exists(Path.Combine(root, "Coder.sln")))
        {
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Coder.sln not found.");
        }

        var directory = Path.Combine(root, "artifacts", "screenshots");
        Directory.CreateDirectory(directory);
        window.CaptureRenderedFrame()?.Save(Path.Combine(directory, $"{name}.png"));
    }

    /// <summary>The question is drawn inside the window (no native frame), blocks shortcuts, and answers.</summary>
    [AvaloniaFact]
    public async Task Dialog_is_an_in_app_modal_that_returns_the_choice()
    {
        var window = new Window { Width = 800, Height = 500, Content = new TextBox() };
        window.Show();
        try
        {
            var dialog = new MessageDialog<bool>(
                "Delete \"g.html\"?", "It goes to the Recycle Bin, where you can restore it.",
                [new("Cancel", false), new("Move to Recycle Bin", true, IsPrimary: true)], cancelResult: false);
            var answer = dialog.ShowAsync(window);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Assert.True(Modal.IsOpen);
            Assert.Equal(800, dialog.Bounds.Width);           // the scrim covers the whole window
            SaveScreenshot(window, "dialog");

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Assert.False(await answer);                        // Escape cancels
            Assert.False(Modal.IsOpen);

            var second = new MessageDialog<bool>("Again?", "Detail.", [new("Cancel", false), new("Yes", true, IsPrimary: true)], false);
            var yes = second.ShowAsync(window);
            second.Buttons.Last().RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Assert.True(await yes);
        }
        finally
        {
            window.Close();
        }
    }
}
