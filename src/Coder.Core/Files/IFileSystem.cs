using System.Text;

namespace Coder.Core.Files;

/// <summary>A file or folder shown in the explorer.</summary>
public sealed record FileEntry(string Path, string Name, bool IsDirectory);

/// <summary>Decoded file contents plus what is needed to write them back byte-for-byte compatible.</summary>
public sealed record TextFileContent(string Text, Encoding Encoding);

/// <summary>File access used by the workbench. Abstracted so view models can be tested without a disk.</summary>
public interface IFileSystem
{
    /// <summary>Immediate children of a folder: folders first, then files, each sorted by name.</summary>
    IReadOnlyList<FileEntry> GetChildren(string directory);

    /// <summary>Files below <paramref name="root"/> for quick-open, skipping build output and VCS folders.</summary>
    IEnumerable<string> EnumerateFiles(string root);

    Task<TextFileContent> ReadTextAsync(string path, CancellationToken cancellationToken = default);

    Task WriteTextAsync(string path, string text, Encoding encoding, CancellationToken cancellationToken = default);

    /// <summary>Whether a file or folder exists at <paramref name="path"/>.</summary>
    bool Exists(string path);

    /// <summary>Creates an empty file (and its folders), replacing an existing one.</summary>
    void CreateFile(string path);

    /// <summary>Moves a file or folder (creating the target's folders), replacing an existing file when <paramref name="overwrite"/>.</summary>
    void Move(string from, string to, bool overwrite);

    /// <summary>Copies a file, or a folder with everything in it, to a path that doesn't exist yet (creating its folders).</summary>
    void Copy(string from, string to);

    /// <summary>Deletes a file, or a folder (with its contents when <paramref name="recursive"/>).</summary>
    void Delete(string path, bool recursive);

    /// <summary>Creates a folder (and its parents).</summary>
    void CreateDirectory(string path);

    /// <summary>Moves a file or folder to the Recycle Bin (the trash), where it can be restored; deletes it where there is none.</summary>
    void MoveToTrash(string path);
}

/// <summary>Raised when a file cannot be shown as text.</summary>
public sealed class BinaryFileException(string path)
    : IOException($"'{System.IO.Path.GetFileName(path)}' is a binary file and can't be opened as text.");
