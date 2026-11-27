using System.Text;

namespace Coder.Core.Files;

public sealed class LocalFileSystem : IFileSystem
{
    /// <summary>Folders that are almost never edited by hand and would flood quick-open.</summary>
    private static readonly HashSet<string> IgnoredFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", ".vs", ".idea", "node_modules", "bin", "obj", "dist", "build", "out",
        "target", "__pycache__", ".venv", "venv", ".next", ".nuget", "packages",
    };

    /// <summary>Hidden from the explorer, like VS Code's default <c>files.exclude</c>.</summary>
    private static readonly HashSet<string> HiddenEntries = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", ".DS_Store", "Thumbs.db",
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public IReadOnlyList<FileEntry> GetChildren(string directory)
    {
        var info = new DirectoryInfo(directory);
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
        return info.EnumerateFileSystemInfos("*", options)
            .Where(entry => !HiddenEntries.Contains(entry.Name))
            .Select(entry => new FileEntry(entry.FullName, entry.Name, entry is DirectoryInfo))
            .OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IEnumerable<string> EnumerateFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
        while (pending.TryPop(out var directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", options))
            {
                yield return file;
            }

            foreach (var child in Directory.EnumerateDirectories(directory, "*", options))
            {
                if (!IgnoredFolders.Contains(Path.GetFileName(child)))
                {
                    pending.Push(child);
                }
            }
        }
    }

    public async Task<TextFileContent> ReadTextAsync(string path, CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var encoding = DetectEncoding(bytes, out var preambleLength);
        if (encoding is UTF8Encoding && LooksBinary(bytes))
        {
            throw new BinaryFileException(path);
        }

        var text = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
        return new TextFileContent(text, encoding);
    }

    public async Task WriteTextAsync(string path, string text, Encoding encoding, CancellationToken cancellationToken = default)
    {
        // Write to a temp file first so a crash mid-save never leaves a half-written file behind.
        var temp = path + ".coder-save";
        await File.WriteAllTextAsync(temp, text, encoding, cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    public bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    public void CreateFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
    }

    public void Move(string from, string to, bool overwrite)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        if (Directory.Exists(from))
        {
            Directory.Move(from, to);
        }
        else
        {
            File.Move(from, to, overwrite);
        }
    }

    public void Copy(string from, string to)
    {
        if (File.Exists(from))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to, overwrite: false);
            return;
        }

        // A folder: its tree, recreated (links are not followed, so a junction can't make this endless).
        var source = new DirectoryInfo(from);
        Directory.CreateDirectory(to);
        foreach (var entry in source.EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget is not null)
            {
                continue;
            }

            Copy(entry.FullName, Path.Combine(to, entry.Name));
        }
    }

    public void Delete(string path, bool recursive)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive);
        }
        else
        {
            File.Delete(path);
        }
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void MoveToTrash(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // The shell's own Recycle Bin (restorable from Explorer), without its progress dialogs.
            if (Directory.Exists(path))
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            else
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }

            return;
        }

        Delete(path, recursive: true);
    }

    private static Encoding DetectEncoding(ReadOnlySpan<byte> bytes, out int preambleLength)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            preambleLength = 3;
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            preambleLength = 2;
            return Encoding.Unicode;
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            preambleLength = 2;
            return Encoding.BigEndianUnicode;
        }

        preambleLength = 0;
        return Utf8NoBom;
    }

    /// <summary>A NUL byte in the first 8 KB is the classic, reliable binary-file signal.</summary>
    private static bool LooksBinary(byte[] bytes) => bytes.AsSpan(0, Math.Min(bytes.Length, 8192)).Contains((byte)0);
}
