using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Coder.App.Workbench;
using Coder.Presentation.Settings;

namespace Coder.App.Tests;

public sealed partial class WorkbenchUiTests
{
    /// <summary>Ctrl+, opens the settings page in a tab; its controls change settings live, and search narrows the list.</summary>
    [AvaloniaFact]
    public void Settings_page_changes_settings_live_and_searches()
    {
        var (window, workbench) = CreateWindow();
        workbench.OpenFolder(RepositoryRoot);
        window.Show();
        workbench.OpenSettingsPage(); // what the gear menu's Settings does
        Dispatcher.UIThread.RunJobs();

        var page = Assert.IsType<SettingsPageViewModel>(workbench.Editors.ActiveTab);
        var view = window.GetVisualDescendants().OfType<SettingsView>().Single();
        Assert.True(view.IsEffectivelyVisible);
        Save(window, "settings-page");

        // The minimap switch turns the minimap off; a command turning it back on shows on the page.
        var minimap = window.GetVisualDescendants().OfType<ToggleSwitch>().Single(t => Avalonia.Automation.AutomationProperties.GetName(t) == "Minimap");
        Assert.True(minimap.IsChecked);
        minimap.IsChecked = false;
        Assert.False(workbench.IsMinimapVisible);
        workbench.IsMinimapVisible = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(minimap.IsChecked);

        // Choosing the right side moves the side bar.
        var side = window.GetVisualDescendants().OfType<ComboBox>().Single(c => Avalonia.Automation.AutomationProperties.GetName(c) == "Primary Side Bar Position");
        side.SelectedIndex = 1;
        Assert.True(workbench.IsSideBarOnRight);

        page.Query = "font";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["Font Family", "Font Size", "Line Height", "Terminal Font Family", "Terminal Font Size"], page.Visible.Select(i => i.Title));
        page.Query = "zzzz";
        Assert.False(page.HasResults);

        // Ctrl+, opens it from anywhere.
        var gesture = Avalonia.Input.KeyGesture.Parse(workbench.Commands.All.Single(c => c.Id == Presentation.Commands.CommandIds.OpenSettingsPage).KeyBinding!);
        Assert.Equal((Avalonia.Input.Key.OemComma, Avalonia.Input.KeyModifiers.Control), (gesture.Key, gesture.KeyModifiers));

        // Opening it again focuses the same tab.
        workbench.OpenSettingsPage();
        Assert.Single(workbench.Editors.Tabs);
    }
}
