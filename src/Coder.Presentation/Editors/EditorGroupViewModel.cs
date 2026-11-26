using System.Collections.ObjectModel;
using System.Text;
using Coder.Core.Files;
using Coder.Presentation.Mvvm;
using Coder.Presentation.Services;

namespace Coder.Presentation.Editors;

/// <summary>The tab strip and the documents behind it.</summary>
public sealed class EditorGroupViewModel(IFileSystem fileSystem, IDialogService dialogs) : ObservableObject
{
    private TabViewModel? _activeTab;

    public ObservableCollection<TabViewModel> Tabs { get; } = [];

    /// <summary>Raised after a tab's document was written to disk.</summary>
    public event EventHandler<EditorTabViewModel>? Saved;

    /// <summary>The open tab for a file, if any.</summary>
    public EditorTabViewModel? FindTab(string path) =>
        Tabs.OfType<EditorTabViewModel>().FirstOrDefault(tab => tab.FilePath is not null && Coder.Core.Lsp.DocumentUri.PathComparer.Equals(tab.FilePath, path));

    /// <summary>The shown tab: a document or a diff.</summary>
    public TabViewModel? ActiveTab
    {
        get => _activeTab;
        set
        {
            var previous = _activeTab;
            if (!SetProperty(ref _activeTab, value))
            {
                return;
            }

            previous?.IsActive = false;
            value?.IsActive = true;
            OnPropertyChanged(nameof(ActiveEditor));
            OnPropertyChanged(nameof(HasTabs));
        }
    }

    /// <summary>The shown document, or null when the active tab is not one (a diff).</summary>
    public EditorTabViewModel? ActiveEditor => ActiveTab as EditorTabViewModel;

    public bool HasTabs => ActiveTab is not null;

    /// <summary>Opens a file, or focuses its tab if it is already open.</summary>
    public async Task OpenAsync(string path)
    {
        var existing = FindTab(path);
        if (existing is not null)
        {
            ActiveTab = existing;
            return;
        }

        if (ImageTabViewModel.IsImage(path))
        {
            var image = Tabs.OfType<ImageTabViewModel>().FirstOrDefault(tab => Coder.Core.Lsp.DocumentUri.PathComparer.Equals(tab.FilePath, path));
            if (image is not null)
            {
                ActiveTab = image;
            }
            else
            {
                Add(new ImageTabViewModel(path));
            }

            return;
        }

        var content = await fileSystem.ReadTextAsync(path);
        Add(new EditorTabViewModel(path, content.Text, content.Encoding));
    }

    /// <summary>Shows a diff. A diff of the same file and side replaces the old one, so it is never stale.</summary>
    public void OpenDiff(DiffTabViewModel diff)
    {
        var existing = Tabs.OfType<DiffTabViewModel>().FirstOrDefault(tab =>
            tab.ModifiedLabel == diff.ModifiedLabel && Coder.Core.Lsp.DocumentUri.PathComparer.Equals(tab.FilePath, diff.FilePath));
        if (existing is null)
        {
            Add(diff);
            return;
        }

        Tabs[Tabs.IndexOf(existing)] = diff;
        existing.Close();
        ActiveTab = diff;
    }

    /// <summary>Shows a tab that is not a file (the settings page): focuses it if it is open, else adds it.</summary>
    public void Show(TabViewModel tab)
    {
        if (Tabs.Contains(tab))
        {
            ActiveTab = tab;
        }
        else
        {
            Add(tab);
        }
    }

    public void NewUntitled() => Add(new EditorTabViewModel(null, string.Empty, new UTF8Encoding(false)));

    public async Task<bool> SaveAsync(EditorTabViewModel tab, bool chooseLocation = false)
    {
        var path = tab.FilePath;
        if (path is null || chooseLocation)
        {
            path = await dialogs.PickSaveLocationAsync(tab.Title);
            if (path is null)
            {
                return false;
            }
        }

        await fileSystem.WriteTextAsync(path, tab.Document.GetTextForSave(), tab.Encoding);
        tab.FilePath = path;
        tab.Document.MarkSaved();
        Saved?.Invoke(this, tab);
        return true;
    }

    /// <summary>Closes a tab, asking to save first when it has changes. Returns false if the user cancelled.</summary>
    public async Task<bool> CloseAsync(TabViewModel tab)
    {
        if (tab is EditorTabViewModel { IsDirty: true } document)
        {
            ActiveTab = tab;
            var choice = await dialogs.ConfirmUnsavedChangesAsync(tab.Title);
            if (choice == UnsavedChangesChoice.Cancel || (choice == UnsavedChangesChoice.Save && !await SaveAsync(document)))
            {
                return false;
            }
        }

        var index = Tabs.IndexOf(tab);
        Tabs.RemoveAt(index);
        tab.Close();
        if (ReferenceEquals(ActiveTab, tab))
        {
            ActiveTab = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];
        }

        return true;
    }

    public async Task<bool> CloseAllAsync()
    {
        foreach (var tab in Tabs.ToList())
        {
            if (!await CloseAsync(tab))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A file or folder was renamed or moved: tabs of it, or of files inside it, follow.</summary>
    public void FollowMove(string from, string to)
    {
        foreach (var tab in Tabs.Where(t => t.FilePath is not null).ToList())
        {
            if (Coder.Core.Lsp.DocumentUri.PathComparer.Equals(tab.FilePath, from))
            {
                tab.FilePath = to;
            }
            else if (tab.FilePath!.StartsWith(from + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                tab.FilePath = to + tab.FilePath[from.Length..];
            }
        }
    }

    /// <summary>A file or folder was deleted: its tabs close, except ones with unsaved changes (the user may still save them).</summary>
    public async Task CloseDeletedAsync(string path)
    {
        foreach (var tab in Tabs.Where(t => !t.IsDirty && t.FilePath is { } file
                     && (Coder.Core.Lsp.DocumentUri.PathComparer.Equals(file, path) || file.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))).ToList())
        {
            await CloseAsync(tab);
        }
    }

    /// <summary>Cycles the active tab by <paramref name="step"/> positions (wrapping around).</summary>
    public void Cycle(int step)
    {
        if (ActiveTab is null || Tabs.Count < 2)
        {
            return;
        }

        var index = (Tabs.IndexOf(ActiveTab) + step + Tabs.Count) % Tabs.Count;
        ActiveTab = Tabs[index];
    }

    private void Add(TabViewModel tab)
    {
        // Insert next to the current tab, which is where the user is looking.
        var index = ActiveTab is null ? Tabs.Count : Tabs.IndexOf(ActiveTab) + 1;
        Tabs.Insert(index, tab);
        ActiveTab = tab;
    }
}
