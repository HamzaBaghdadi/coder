using Coder.Presentation.Editors;

namespace Coder.Presentation.Workbench;

/// <summary>"Open Preview" (Ctrl+Shift+V) for Markdown and SVG files, and back to the source from a preview.</summary>
public sealed partial class WorkbenchViewModel
{
    /// <summary>Whether the active tab is a file that has a preview (the editor shows a button for it).</summary>
    public bool CanPreviewActive => Editors.ActiveTab is EditorTabViewModel { FilePath: { } path } && PreviewTabViewModel.KindOf(path) is not null;

    /// <summary>Previews the active Markdown or SVG file; from a preview, shows its source.</summary>
    public async Task OpenPreviewAsync()
    {
        switch (Editors.ActiveTab)
        {
            case EditorTabViewModel { FilePath: { } path } when PreviewTabViewModel.KindOf(path) is not null:
                await RunSafelyAsync(() => Editors.OpenPreviewAsync(path));
                break;
            case PreviewTabViewModel preview:
                await RunSafelyAsync(() => Editors.OpenSourceAsync(preview));
                break;
            default:
                StatusBar.Message = "Open Preview works on Markdown (.md) and SVG files.";
                break;
        }
    }

    /// <summary>An extension's page (details and README) in the editor area; one tab per extension.</summary>
    public void ShowExtensionDetails(Extensions.ExtensionItemViewModel item)
    {
        var open = Editors.Tabs.OfType<Extensions.ExtensionDetailsTabViewModel>()
            .FirstOrDefault(tab => string.Equals(tab.Item.Id, item.Id, StringComparison.OrdinalIgnoreCase));
        if (open is not null && ReferenceEquals(open.Item, item))
        {
            Editors.Show(open);
            return;
        }

        if (open is not null)
        {
            _ = Editors.CloseAsync(open); // the row was replaced (installed or uninstalled since): show it fresh
        }

        Editors.Show(new Extensions.ExtensionDetailsTabViewModel(item, () => Extensions.LoadReadmeAsync(item)));
    }

    public async Task OpenPreviewSourceAsync()
    {
        if (Editors.ActiveTab is PreviewTabViewModel preview)
        {
            await RunSafelyAsync(() => Editors.OpenSourceAsync(preview));
        }
    }
}
