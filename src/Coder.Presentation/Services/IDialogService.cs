namespace Coder.Presentation.Services;

public enum UnsavedChangesChoice
{
    Save,
    DontSave,
    Cancel,
}

/// <summary>Native dialogs, implemented by the UI layer.</summary>
public interface IDialogService
{
    Task<string?> PickFolderAsync();

    Task<string?> PickFileAsync();

    Task<string?> PickSaveLocationAsync(string suggestedName);

    Task<UnsavedChangesChoice> ConfirmUnsavedChangesAsync(string fileName);

    /// <summary>Asks a yes/no question; returns true when the user picks <paramref name="confirmLabel"/>.</summary>
    Task<bool> ConfirmAsync(string message, string detail, string confirmLabel);
}
