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
        await RefreshPreviewsAsync(path); // an open preview follows the new editor tab
    }

    /// <summary>
    /// Opens (or shows) the rendered preview of a Markdown or SVG file next to the current tab. It follows the
    /// file's editor tab while one is open, and the file on disk otherwise. Does nothing for other files.
    /// </summary>
    public async Task OpenPreviewAsync(string path)
    {
        if (PreviewTabViewModel.KindOf(path) is not { } kind)
        {
            return;
        }

        if (Tabs.OfType<PreviewTabViewModel>().FirstOrDefault(tab => Coder.Core.Lsp.DocumentUri.PathComparer.Equals(tab.FilePath, path)) is { } existing)
        {
            ActiveTab = existing;
            return;
        }

        var source = FindTab(path);
        var text = source?.Document.GetText() ?? (await fileSystem.ReadTextAsync(path)).Text;
        var preview = new PreviewTabViewModel(path, kind, text);
        preview.Follow(source);
        Add(preview);
    }

    /// <summary>"Open Source" from a preview: the file's editor tab, opening it if needed.</summary>
    public async Task OpenSourceAsync(PreviewTabViewModel preview)
    {
        if (preview.FilePath is { } path)
        {
            await OpenAsync(path);
            preview.Follow(FindTab(path));
        }
    }

    /// <summary>Keeps previews following their file: its editor tab when one opens or closes, the disk on save.</summary>
    private async Task RefreshPreviewsAsync(string path)
    {
        foreach (var preview in Tabs.OfType<PreviewTabViewModel>().Where(tab => Coder.Core.Lsp.DocumentUri.PathComparer.Equals(tab.FilePath, path)).ToList())
        {
            if (FindTab(path) is { } source)
            {
                preview.Follow(source);
            }
            else
            {
                preview.Follow(null);
                try
                {
                    preview.Text = (await fileSystem.ReadTextAsync(path)).Text;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // The file went away: the preview keeps showing the last text it had.
                }
            }
        }
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

    /// <summary>Indentation for files that don't show their own (the Tab Size and Insert Spaces settings).</summary>
    public Func<Coder.Core.Editing.EditorOptions> DefaultOptions { get; set; } = () => new();

    /// <summary>Whether a file's own indentation (tabs, 2 or 4 spaces…) overrides the defaults when it opens.</summary>
    public Func<bool> DetectIndentation { get; set; } = () => true;

    /// <summary>
    /// Runs just before a tab is written: save clean-ups and format on save. Its flag says whether the save was
    /// automatic (auto save), which skips formatting, as in VS Code.
    /// </summary>
    public Func<EditorTabViewModel, bool, Task>? BeforeSave { get; set; }

    /// <summary>Gives a tab its indentation: the defaults, or what its text uses when detection is on.</summary>
    public void ApplyOptions(EditorTabViewModel tab)
    {
        var options = DefaultOptions() with { AutoClosePairs = tab.Editor.Options.AutoClosePairs };
        if (DetectIndentation() && Coder.Core.Editing.Indentation.Detect(tab.Document) is { } detected)
        {
            options = options with { InsertSpaces = detected.InsertSpaces, TabSize = detected.InsertSpaces ? detected.TabSize : options.TabSize };
        }

        tab.Editor.Options = options;
        tab.NotifyOptionsChanged();
    }

    /// <summary>The indentation settings changed: every open document takes them again.</summary>
    public void ReapplyOptions()
    {
        foreach (var tab in Tabs.OfType<EditorTabViewModel>())
        {
            ApplyOptions(tab);
        }
    }

    public async Task<bool> SaveAsync(EditorTabViewModel tab, bool chooseLocation = false, bool automatic = false)
    {
        if (BeforeSave is not null)
        {
            await BeforeSave(tab, automatic);
        }

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
        await RefreshPreviewsAsync(path);
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
        if (tab is EditorTabViewModel { FilePath: { } closedPath })
        {
            await RefreshPreviewsAsync(closedPath); // its preview goes back to the file on disk
        }

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
        if (tab is EditorTabViewModel document)
        {
            ApplyOptions(document);
        }

        // Insert next to the current tab, which is where the user is looking.
        var index = ActiveTab is null ? Tabs.Count : Tabs.IndexOf(ActiveTab) + 1;
        Tabs.Insert(index, tab);
        ActiveTab = tab;
    }
}
