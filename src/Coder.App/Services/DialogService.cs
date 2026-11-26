using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Coder.Presentation.Services;

namespace Coder.App.Services;

/// <summary>Native file pickers and modal questions, shown over the main window.</summary>
public sealed class DialogService(Func<Window> owner) : IDialogService
{
    public async Task<string?> PickFolderAsync()
    {
        var folders = await owner().StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Open Folder" });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickFileAsync()
    {
        var files = await owner().StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Open File" });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSaveLocationAsync(string suggestedName)
    {
        var file = await owner().StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save As",
            SuggestedFileName = suggestedName,
        });
        return file?.TryGetLocalPath();
    }

    public Task<UnsavedChangesChoice> ConfirmUnsavedChangesAsync(string fileName) =>
        new MessageDialog<UnsavedChangesChoice>(
            $"Do you want to save the changes you made to {fileName}?",
            "Your changes will be lost if you don't save them.",
            [
                new("Don't Save", UnsavedChangesChoice.DontSave),
                new("Cancel", UnsavedChangesChoice.Cancel),
                new("Save", UnsavedChangesChoice.Save, IsPrimary: true),
            ],
            cancelResult: UnsavedChangesChoice.Cancel).ShowDialog<UnsavedChangesChoice>(owner());

    public Task<bool> ConfirmAsync(string message, string detail, string confirmLabel) =>
        new MessageDialog<bool>(message, detail,
            [new("Cancel", false), new(confirmLabel, true, IsPrimary: true)],
            cancelResult: false).ShowDialog<bool>(owner());
}
