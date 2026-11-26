using Coder.Core.Editing;
using Coder.Core.Files;
using Coder.Core.Lsp;
using Coder.Core.Syntax;
using Coder.Core.Text;
using Coder.Presentation.Editors;

namespace Coder.Presentation.LanguageFeatures;

/// <summary>
/// Applies a language server's edits (rename, quick fix, refactorings) across files, step by step in
/// the server's order. Files open in an editor change there, as one undo step each, and are left unsaved
/// for review, like Replace in Files; other files are changed on disk, keeping their encoding and line
/// endings. Files are also created, renamed (an open tab follows its file) and deleted.
/// </summary>
public sealed class WorkspaceEditApplier(IFileSystem fileSystem, EditorGroupViewModel editors, LanguageServices languages)
{
    /// <summary>Applies every step; returns how many files changed.</summary>
    /// <exception cref="IOException">A step can't be done (a file to create exists, one to delete is missing…). The steps before it stay done.</exception>
    public async Task<int> ApplyAsync(WorkspaceEdit edit)
    {
        var touched = new HashSet<string>(DocumentUri.PathComparer);
        var onDisk = new List<(string Path, FileChangeType Type)>();
        try
        {
            foreach (var change in edit.Changes)
            {
                switch (change)
                {
                    case FileEdit { Edits.Count: > 0 } file:
                        if (await EditAsync(file))
                        {
                            onDisk.Add((file.Path, FileChangeType.Changed));
                        }

                        touched.Add(file.Path);
                        break;
                    case CreateFile create when Create(create):
                        onDisk.Add((create.Path, FileChangeType.Created));
                        touched.Add(create.Path);
                        break;
                    case RenameFile rename when Rename(rename):
                        onDisk.Add((rename.OldPath, FileChangeType.Deleted));
                        onDisk.Add((rename.NewPath, FileChangeType.Created));
                        touched.Add(rename.NewPath);
                        break;
                    case DeleteFile delete when await DeleteAsync(delete):
                        onDisk.Add((delete.Path, FileChangeType.Deleted));
                        touched.Add(delete.Path);
                        break;
                }
            }
        }
        finally
        {
            // Servers only know closed files from disk; without this they keep the old text (and report
            // errors such as "undefined" after a rename).
            languages.NotifyFilesChanged(onDisk);
        }

        return touched.Count;
    }

    /// <summary>Edits the open tab, or the file on disk. Returns true when the disk was written.</summary>
    private async Task<bool> EditAsync(FileEdit file)
    {
        if (editors.FindTab(file.Path) is { } tab)
        {
            ApplyTo(tab.Editor.Document, file.Edits, tab.Editor.ApplyEdits);
            return false;
        }

        var content = await fileSystem.ReadTextAsync(file.Path);
        var document = new TextDocument(content.Text);
        ApplyTo(document, file.Edits, new EditorState(document, Languages.PlainText).ApplyEdits);
        await fileSystem.WriteTextAsync(file.Path, document.GetTextForSave(), content.Encoding);
        return true;
    }

    private bool Create(CreateFile create)
    {
        if (fileSystem.Exists(create.Path))
        {
            if (create.IgnoreIfExists && !create.Overwrite)
            {
                return false;
            }

            if (!create.Overwrite)
            {
                throw new IOException($"{Path.GetFileName(create.Path)} already exists.");
            }
        }

        fileSystem.CreateFile(create.Path);
        return true;
    }

    private bool Rename(RenameFile rename)
    {
        if (fileSystem.Exists(rename.NewPath) && !rename.Overwrite)
        {
            return rename.IgnoreIfExists ? false : throw new IOException($"{Path.GetFileName(rename.NewPath)} already exists.");
        }

        fileSystem.Move(rename.OldPath, rename.NewPath, rename.Overwrite);

        editors.FollowMove(rename.OldPath, rename.NewPath); // tabs follow, including files inside a renamed folder
        return true;
    }

    /// <summary>Deletes the file; its tab closes unless it has unsaved changes (then the user decides).</summary>
    private async Task<bool> DeleteAsync(DeleteFile delete)
    {
        if (!fileSystem.Exists(delete.Path))
        {
            return delete.IgnoreIfNotExists ? false : throw new IOException($"{Path.GetFileName(delete.Path)} does not exist.");
        }

        fileSystem.Delete(delete.Path, delete.Recursive);
        await editors.CloseDeletedAsync(delete.Path);

        return true;
    }

    /// <summary>Converts the edits' line/column ranges to offsets in <paramref name="document"/>, then applies them.</summary>
    private static void ApplyTo(TextDocument document, IReadOnlyList<TextReplacement> edits, Action<IReadOnlyList<(int Start, int End, string Text)>> apply) =>
        apply(edits.Select(e => (document.GetOffset(e.Range.Start), document.GetOffset(e.Range.End), LineEndings.Normalize(e.NewText))).ToList());
}
