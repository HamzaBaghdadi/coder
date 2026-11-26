using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Coder.App.Controls;
using Coder.App.Mvvm;
using Coder.App.Theme;
using Coder.Presentation.Mvvm;
using Coder.Presentation.Search;

namespace Coder.App.Workbench;

/// <summary>
/// The "Aa / ab / .*" toggles shown inside a search field, plus their Alt+C / Alt+W / Alt+R keys.
/// Shared by find-in-file and search-in-files.
/// </summary>
public static class SearchOptionToggles
{
    public static StackPanel Create(ISearchOptions options) => new()
    {
        Orientation = Orientation.Horizontal,
        Margin = new Thickness(0, 0, 2, 0),
        Children =
        {
            Toggle(options, "Aa", "Match Case (Alt+C)", o => o.MatchCase, o => o.MatchCase = !o.MatchCase),
            Toggle(options, "ab", "Match Whole Word (Alt+W)", o => o.WholeWord, o => o.WholeWord = !o.WholeWord),
            Toggle(options, ".*", "Use Regular Expression (Alt+R)", o => o.UseRegex, o => o.UseRegex = !o.UseRegex),
        },
    };

    /// <summary>Handles Alt+C / Alt+W / Alt+R. Returns true when the key toggled an option.</summary>
    public static bool TryHandleKey(ISearchOptions options, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Alt)
        {
            return false;
        }

        switch (e.Key)
        {
            case Key.C:
                options.MatchCase = !options.MatchCase;
                return true;
            case Key.W:
                options.WholeWord = !options.WholeWord;
                return true;
            case Key.R:
                options.UseRegex = !options.UseRegex;
                return true;
            default:
                return false;
        }
    }

    private static FlatButton Toggle(ISearchOptions options, string glyph, string name, Func<ISearchOptions, bool> isOn, Action<ISearchOptions> toggle)
    {
        var button = new FlatButton
        {
            Width = 24,
            Height = 22,
            Padding = new Thickness(0),
            FontFamily = Typography.Code,
            FontSize = Typography.SmallSize,
            Content = glyph,
            Command = new RelayCommand(() => toggle(options)),
            Focusable = false, // The keyboard path is Alt+letter, so focus stays in the field.
        };
        button.Classes.Add("toggle");
        button.BindClass("checked", options, isOn);
        AutomationProperties.SetName(button, name);
        ToolTip.SetTip(button, name);
        return button;
    }
}
