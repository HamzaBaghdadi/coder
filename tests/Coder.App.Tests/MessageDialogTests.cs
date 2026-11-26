using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Coder.App.Controls;
using Coder.App.Services;
using Coder.App.Theme;

namespace Coder.App.Tests;

public sealed class MessageDialogTests
{
    /// <summary>Regression: the primary button once had a local null foreground, which hid its label.</summary>
    [AvaloniaFact]
    public void Every_dialog_button_shows_its_label()
    {
        var dialog = new MessageDialog<bool>(
            "Discard all 2 changes?", "This can't be undone.",
            [new("Cancel", false), new("Discard All", true, IsPrimary: true)], cancelResult: false);
        dialog.Show();
        try
        {
            var buttons = dialog.GetLogicalDescendants().OfType<FlatButton>().ToList();
            Assert.Equal(2, buttons.Count);

            var primary = Assert.Single(buttons, b => b.Classes.Contains("primary"));
            Assert.Equal("Discard All", primary.Content);
            Assert.Same(Palette.TextOnAccent, primary.Foreground);
            Assert.All(buttons, b => Assert.NotNull(b.Foreground));
        }
        finally
        {
            dialog.Close();
        }
    }
}
