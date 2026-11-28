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

    /// <summary>Picks files to open (several when <paramref name="multiple"/>); empty when cancelled.</summary>
    async Task<IReadOnlyList<string>> PickFilesAsync(string title, bool multiple) =>
        await PickFileAsync() is { } path ? [path] : [];

    Task<string?> PickSaveLocationAsync(string suggestedName);

    Task<UnsavedChangesChoice> ConfirmUnsavedChangesAsync(string fileName);

    /// <summary>Asks a yes/no question; returns true when the user picks <paramref name="confirmLabel"/>.</summary>
    Task<bool> ConfirmAsync(string message, string detail, string confirmLabel);

    /// <summary>
    /// Asks a question with the given choices, the first being the default button. Returns the index of the
    /// choice picked, or null when the user dismisses it (Escape) or <paramref name="cancellationToken"/> withdraws it.
    /// </summary>
    Task<int?> ChooseAsync(string message, string detail, IReadOnlyList<string> choices, CancellationToken cancellationToken = default);
}
