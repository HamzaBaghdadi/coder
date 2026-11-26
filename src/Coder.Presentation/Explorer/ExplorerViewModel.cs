using System.Collections.ObjectModel;
using Coder.Core.Files;
using Coder.Core.Lsp;
using Coder.Presentation.Mvvm;
using Coder.Presentation.Services;

namespace Coder.Presentation.Explorer;

/// <summary>
/// The file tree, kept as a flat list of visible rows. Expanding a folder inserts its children right
/// after it; collapsing removes them. A flat list virtualizes perfectly, so huge folders stay fast and
/// only folders the user actually opens are ever read from disk.
/// <para>
/// It also changes the files themselves, like VS Code's explorer: new files and folders (named in place),
/// rename, delete to the Recycle Bin and move (drag and drop). It follows changes on disk
/// (<see cref="Refresh"/>), keeping the open folders and the selection.
/// </para>
/// </summary>
public sealed class ExplorerViewModel(IFileSystem fileSystem) : ObservableObject
{
    private string? _rootPath;
    private ExplorerItemViewModel? _selectedItem;

    /// <summary>Raised when the user activates a file row.</summary>
    public event EventHandler<string>? FileActivated;

    /// <summary>A file or folder was renamed or moved: open tabs follow it.</summary>
    public event EventHandler<(string From, string To)>? PathMoved;

    /// <summary>A file or folder went to the Recycle Bin: its open tabs close (unless they have unsaved changes).</summary>
    public event EventHandler<string>? PathDeleted;

    /// <summary>A row should come into view (revealed, or just created).</summary>
    public event EventHandler<ExplorerItemViewModel>? ScrollRequested;

    /// <summary>Something went wrong (a name taken, no permission), for the status bar.</summary>
    public event EventHandler<string>? Notice;

    /// <summary>Asks before deleting and moving; null in tests that don't need to.</summary>
    public IDialogService? Dialogs { get; set; }

    public ObservableCollection<ExplorerItemViewModel> Rows { get; } = [];

    public string? RootPath
    {
        get => _rootPath;
        private set
        {
            if (SetProperty(ref _rootPath, value))
            {
                OnPropertyChanged(nameof(RootName));
                OnPropertyChanged(nameof(HasFolder));
            }
        }
    }

    public string RootName => RootPath is null ? "No folder opened" : Path.GetFileName(RootPath.TrimEnd(Path.DirectorySeparatorChar)).ToUpperInvariant();

    public bool HasFolder => RootPath is not null;

    public ExplorerItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set => SetProperty(ref _selectedItem, value);
    }

    public void OpenFolder(string path)
    {
        RootPath = path;
        Rows.Clear();
        foreach (var entry in fileSystem.GetChildren(path))
        {
            Rows.Add(new ExplorerItemViewModel(entry, 0));
        }
    }

    /// <summary>Enter / click: toggles folders, opens files.</summary>
    public void Activate(ExplorerItemViewModel item)
    {
        if (item.IsEditing)
        {
            return;
        }

        if (item.IsDirectory)
        {
            SetExpanded(item, !item.IsExpanded);
        }
        else
        {
            FileActivated?.Invoke(this, item.Path);
        }
    }

    public void SetExpanded(ExplorerItemViewModel folder, bool expand)
    {
        if (!folder.IsDirectory || folder.IsExpanded == expand)
        {
            return;
        }

        if (expand)
        {
            Expand(folder);
        }
        else
        {
            Collapse(folder);
        }
    }

    /// <summary>Left arrow on a file or collapsed folder selects its parent folder, like VS Code.</summary>
    public void SelectParent(ExplorerItemViewModel item)
    {
        if (Parent(item) is { } parent)
        {
            SelectedItem = parent;
        }
    }

    /// <summary>Closes every folder.</summary>
    public void CollapseAll()
    {
        foreach (var folder in Rows.Where(r => r.Depth == 0 && r.IsExpanded).ToList())
        {
            Collapse(folder);
        }
    }

    /// <summary>
    /// Reads the tree again (files changed on disk): the same folders stay open and the same row stays
    /// selected when they still exist. A row being named is kept as it is.
    /// </summary>
    public void Refresh()
    {
        if (RootPath is null || Rows.Any(r => r.IsEditing))
        {
            return;
        }

        // Only rows that changed are added or removed, so the list keeps its scroll position.
        var selected = SelectedItem;
        Sync(null);
        if (selected is not null && !Rows.Contains(selected))
        {
            SelectedItem = null;
        }
    }

    /// <summary>Brings one folder's rows (the root's when null) in line with the disk, then its open subfolders.</summary>
    private void Sync(ExplorerItemViewModel? folder)
    {
        IReadOnlyList<FileEntry> entries;
        try
        {
            entries = fileSystem.GetChildren(folder?.Path ?? RootPath!);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return; // gone or locked: leave it as it is until it is back
        }

        var depth = folder is null ? 0 : folder.Depth + 1;
        var children = folder?.VisibleChildren ?? Rows.Where(r => r.Depth == 0 && !r.IsPending).ToList();
        var wanted = entries.Select(e => e.Path).ToHashSet(DocumentUri.PathComparer);

        // Gone from disk: remove (with everything shown under it).
        foreach (var row in children.Where(r => !wanted.Contains(r.Path)).ToList())
        {
            if (row.IsExpanded)
            {
                Collapse(row);
            }

            Rows.Remove(row);
            children.Remove(row);
        }

        // New on disk: insert in the folder's order (folders first, then files, by name).
        var byPath = children.ToDictionary(r => r.Path, DocumentUri.PathComparer);
        var ordered = new List<ExplorerItemViewModel>(entries.Count);
        var insertAt = folder is null ? 0 : Rows.IndexOf(folder) + 1;
        foreach (var entry in entries)
        {
            if (byPath.TryGetValue(entry.Path, out var existing))
            {
                ordered.Add(existing);
                insertAt = SubtreeEnd(existing);
                continue;
            }

            var row = new ExplorerItemViewModel(entry, depth);
            Rows.Insert(insertAt++, row);
            ordered.Add(row);
        }

        if (folder is not null)
        {
            folder.VisibleChildren.Clear();
            folder.VisibleChildren.AddRange(ordered);
        }

        foreach (var row in ordered.Where(r => r.IsExpanded))
        {
            Sync(row);
        }
    }

    /// <summary>The index just past a row and everything shown under it.</summary>
    private int SubtreeEnd(ExplorerItemViewModel row)
    {
        var index = Rows.IndexOf(row) + 1;
        while (index < Rows.Count && Rows[index].Depth > row.Depth)
        {
            index++;
        }

        return index;
    }

    /// <summary>
    /// Shows a file in the tree: opens the folders down to it and selects it (when a file is opened from
    /// anywhere, like VS Code's explorer). Returns false when it isn't under the open folder.
    /// </summary>
    public bool Reveal(string path)
    {
        if (RootPath is not { } root || !IsUnder(path, root))
        {
            return false;
        }

        var relative = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = root;
        ExplorerItemViewModel? row = null;
        foreach (var part in relative)
        {
            if (row is { IsDirectory: true })
            {
                SetExpanded(row, true);
            }

            current = Path.Combine(current, part);
            row = Rows.FirstOrDefault(r => DocumentUri.PathComparer.Equals(r.Path, current));
            if (row is null)
            {
                return false; // not on disk yet (an unsaved new file) or filtered out
            }
        }

        SelectedItem = row;
        ScrollRequested?.Invoke(this, row!);
        return true;
    }

    // File operations -----------------------------------------------------------------------------

    /// <summary>
    /// New File / New Folder: an empty row to type the name in, at the top of the folder that is
    /// selected (or holds the selected file), else at the top of the tree.
    /// </summary>
    public ExplorerItemViewModel? StartNew(bool directory, ExplorerItemViewModel? near = null)
    {
        if (RootPath is not { } root)
        {
            return null;
        }

        CancelEditing();
        near ??= SelectedItem;
        var folder = near is { IsDirectory: true } ? near : near is null ? null : Parent(near);
        if (folder is not null)
        {
            SetExpanded(folder, true);
        }

        var parentPath = folder?.Path ?? root;
        var row = new ExplorerItemViewModel(new FileEntry(Path.Combine(parentPath, string.Empty), string.Empty, directory), folder is null ? 0 : folder.Depth + 1)
        {
            IsPending = true,
            ParentFolder = parentPath,
            IsEditing = true,
        };
        Rows.Insert(folder is null ? 0 : Rows.IndexOf(folder) + 1, row);
        SelectedItem = row;
        ScrollRequested?.Invoke(this, row);
        return row;
    }

    /// <summary>F2: the row's name becomes editable.</summary>
    public void StartRename(ExplorerItemViewModel item)
    {
        CancelEditing();
        item.IsEditing = true;
        SelectedItem = item;
    }

    /// <summary>Esc: a new row goes away, a rename keeps the old name.</summary>
    public void CancelEditing()
    {
        foreach (var row in Rows.Where(r => r.IsEditing).ToList())
        {
            row.IsEditing = false;
            if (row.IsPending)
            {
                Rows.Remove(row);
            }
        }
    }

    /// <summary>
    /// Enter (or leaving the box): creates the new file or folder, or renames. A new file opens. Returns
    /// false, keeping the box, when the name can't be used (it says why).
    /// </summary>
    public bool CommitEdit(ExplorerItemViewModel item, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || (!item.IsPending && name == item.Name))
        {
            CancelEditing();
            return true;
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars().Except(['/', '\\']).ToArray()) >= 0 || name is "." or "..")
        {
            Notice?.Invoke(this, $"\"{name}\" is not a valid name.");
            return false;
        }

        var folder = item.IsPending ? item.ParentFolder! : Path.GetDirectoryName(item.Path)!;
        var target = Path.GetFullPath(Path.Combine(folder, name)); // "src/app.ts" creates the folders too, as in VS Code
        if (fileSystem.Exists(target) && !(OperatingSystem.IsWindows() && string.Equals(target, item.Path, StringComparison.OrdinalIgnoreCase)))
        {
            Notice?.Invoke(this, $"\"{name}\" already exists here.");
            return false;
        }

        try
        {
            if (item.IsPending && item.IsDirectory)
            {
                fileSystem.CreateDirectory(target);
            }
            else if (item.IsPending)
            {
                fileSystem.CreateFile(target);
            }
            else
            {
                fileSystem.Move(item.Path, target, overwrite: false);
                PathMoved?.Invoke(this, (item.Path, target));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Notice?.Invoke(this, exception.Message);
            return false;
        }

        item.IsEditing = false;
        if (item.IsPending)
        {
            Rows.Remove(item); // the real row comes with the refresh
        }

        Refresh();
        Reveal(target);
        if (item.IsPending && !item.IsDirectory)
        {
            FileActivated?.Invoke(this, target);
        }

        return true;
    }

    /// <summary>Delete: after confirming, the file or folder goes to the Recycle Bin (it can be restored there).</summary>
    public async Task DeleteAsync(ExplorerItemViewModel item)
    {
        if (item.IsPending)
        {
            return;
        }

        var what = item.IsDirectory ? $"the folder \"{item.Name}\" and its contents" : $"\"{item.Name}\"";
        if (Dialogs is not null && !await Dialogs.ConfirmAsync($"Delete {what}?", "It goes to the Recycle Bin, where you can restore it.", "Move to Recycle Bin"))
        {
            return;
        }

        try
        {
            var index = Rows.IndexOf(item);
            fileSystem.MoveToTrash(item.Path);
            PathDeleted?.Invoke(this, item.Path);
            Refresh();
            SelectedItem = Rows.Count == 0 ? null : Rows[Math.Clamp(index, 0, Rows.Count - 1)];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            Notice?.Invoke(this, $"Couldn't delete {item.Name}: {exception.Message}");
        }
    }

    /// <summary>Whether <paramref name="item"/> can be dropped on <paramref name="target"/> (null: the top of the tree).</summary>
    public bool CanMove(ExplorerItemViewModel item, ExplorerItemViewModel? target)
    {
        var folder = TargetFolder(target);
        return folder is not null && !item.IsPending
               && !DocumentUri.PathComparer.Equals(Path.GetDirectoryName(item.Path), folder)   // already there
               && !(item.IsDirectory && (IsUnder(folder, item.Path) || DocumentUri.PathComparer.Equals(folder, item.Path))); // into itself
    }

    /// <summary>Drag and drop: moves a file or folder into a folder, after confirming. Open tabs follow it.</summary>
    public async Task MoveAsync(ExplorerItemViewModel item, ExplorerItemViewModel? target)
    {
        if (!CanMove(item, target) || TargetFolder(target) is not { } folder)
        {
            return;
        }

        var destination = Path.Combine(folder, item.Name);
        if (fileSystem.Exists(destination))
        {
            Notice?.Invoke(this, $"\"{item.Name}\" already exists in {Path.GetFileName(folder)}.");
            return;
        }

        if (Dialogs is not null && !await Dialogs.ConfirmAsync($"Move \"{item.Name}\" into \"{Path.GetFileName(folder)}\"?", string.Empty, "Move"))
        {
            return;
        }

        try
        {
            fileSystem.Move(item.Path, destination, overwrite: false);
            PathMoved?.Invoke(this, (item.Path, destination));
            Refresh();
            Reveal(destination);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Notice?.Invoke(this, $"Couldn't move {item.Name}: {exception.Message}");
        }
    }

    /// <summary>The folder a drop lands in: the folder itself, a file's folder, or the root.</summary>
    private string? TargetFolder(ExplorerItemViewModel? target) =>
        target is null ? RootPath : target.IsDirectory ? target.Path : Path.GetDirectoryName(target.Path);

    private ExplorerItemViewModel? Parent(ExplorerItemViewModel item)
    {
        var index = Rows.IndexOf(item);
        for (var i = index - 1; i >= 0; i--)
        {
            if (Rows[i].Depth < item.Depth)
            {
                return Rows[i];
            }
        }

        return null;
    }

    private static bool IsUnder(string path, string folder)
    {
        var relative = Path.GetRelativePath(folder, path);
        return relative != "." && !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private void Expand(ExplorerItemViewModel folder)
    {
        IReadOnlyList<FileEntry> children;
        try
        {
            children = fileSystem.GetChildren(folder.Path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return; // Unreadable folders simply stay collapsed.
        }

        folder.IsExpanded = true;
        var insertAt = Rows.IndexOf(folder) + 1;
        foreach (var entry in children)
        {
            var child = new ExplorerItemViewModel(entry, folder.Depth + 1);
            folder.VisibleChildren.Add(child);
            Rows.Insert(insertAt++, child);
        }
    }

    private void Collapse(ExplorerItemViewModel folder)
    {
        foreach (var child in folder.VisibleChildren)
        {
            if (child.IsExpanded)
            {
                Collapse(child);
            }

            Rows.Remove(child);
        }

        folder.VisibleChildren.Clear();
        folder.IsExpanded = false;
        foreach (var pending in Rows.Where(r => r.IsPending && DocumentUri.PathComparer.Equals(r.ParentFolder, folder.Path)).ToList())
        {
            Rows.Remove(pending);
        }
    }
}
