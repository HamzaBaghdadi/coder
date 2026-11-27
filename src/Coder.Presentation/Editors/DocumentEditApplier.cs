using System.Text;
using Coder.Core.Files;
using Coder.Core.Lsp;
using Coder.Core.Text;

namespace Coder.Presentation.Editors;

/// <summary>
/// Writes an accepted change to a file, the way an edit by hand would land: an open file changes in its
/// editor (one undo step) and is saved; a closed one is rewritten on disk keeping its encoding and line
/// endings; a new one is created with its folders. Shared by the agent and extension diffs.
/// </summary>
public sealed class DocumentEditApplier(EditorGroupViewModel editors, IFileSystem fileSystem, Action<IReadOnlyList<(string Path, FileChangeType Type)>> filesChanged)
{
    /// <exception cref="IOException">The file couldn't be written or saved.</exception>
    public async Task ApplyAsync(string path, string proposed)
    {
        if (editors.FindTab(path) is { } tab)
        {
            var (start, end, text) = MinimalEdit(tab.Document.GetText(), proposed);
            tab.Editor.ApplyEdits([(start, end, text)]);
            if (!await editors.SaveAsync(tab))
            {
                throw new IOException($"{Path.GetFileName(path)} was changed in its editor but could not be saved.");
            }
        }
        else if (fileSystem.Exists(path))
        {
            var content = await fileSystem.ReadTextAsync(path);
            await fileSystem.WriteTextAsync(path, LineEndings.Apply(proposed, LineEndings.Detect(content.Text)), content.Encoding);
            filesChanged([(path, FileChangeType.Changed)]);
        }
        else
        {
            if (Path.GetDirectoryName(path) is { } folder)
            {
                fileSystem.CreateDirectory(folder);
            }

            await fileSystem.WriteTextAsync(path, LineEndings.Apply(proposed, LineEndings.Platform), new UTF8Encoding(false));
            filesChanged([(path, FileChangeType.Created)]);
        }
    }

    /// <summary>The one replacement that turns <paramref name="before"/> into <paramref name="after"/>: everything between their common start and end.</summary>
    public static (int Start, int End, string Text) MinimalEdit(string before, string after)
    {
        var prefix = 0;
        var max = Math.Min(before.Length, after.Length);
        while (prefix < max && before[prefix] == after[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < max - prefix && before[before.Length - 1 - suffix] == after[after.Length - 1 - suffix])
        {
            suffix++;
        }

        return (prefix, before.Length - suffix, after[prefix..(after.Length - suffix)]);
    }
}
