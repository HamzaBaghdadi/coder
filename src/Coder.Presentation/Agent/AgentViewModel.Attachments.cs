using System.Text;
using Coder.Core.Ai;
using Coder.Core.Files;
using Coder.Core.Text;

namespace Coder.Presentation.Agent;

public sealed partial class AgentViewModel
{
    /// <summary>
    /// Attaches files and folders (dropped from the explorer, a tab or Windows, or picked). Images become
    /// image attachments; binary files are refused with a notice; a path already attached is skipped.
    /// </summary>
    public async Task AddPathsAsync(IEnumerable<string> paths)
    {
        var refused = new List<string>();
        foreach (var path in paths)
        {
            if (Attachments.Any(a => a.Path is { } existing && Coder.Core.Lsp.DocumentUri.PathComparer.Equals(existing, path)))
            {
                continue;
            }

            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (Directory.Exists(path))
            {
                Attachments.Add(new AgentAttachment(AttachmentKind.Folder, name) { Path = path });
            }
            else if (AgentAttachment.ImageMediaType(path) is { } mediaType)
            {
                try
                {
                    var info = new FileInfo(path);
                    if (info.Length > MaxImageBytes)
                    {
                        refused.Add($"{name} is larger than {MaxImageBytes / 1024 / 1024} MB");
                        continue;
                    }

                    AddImage(name, mediaType, await File.ReadAllBytesAsync(path), path);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    refused.Add($"{name}: {exception.Message}");
                }
            }
            else if (File.Exists(path) || _workspace.Editors.FindTab(path) is not null)
            {
                if (_workspace.Editors.FindTab(path) is null && LooksBinary(path))
                {
                    refused.Add($"{name} is a binary file");
                    continue;
                }

                Attachments.Add(new AgentAttachment(AttachmentKind.File, name) { Path = path });
            }
            else
            {
                refused.Add($"{name} was not found");
            }
        }

        Notice = refused.Count == 0 ? null : $"Not attached: {string.Join("; ", refused)}.";
    }

    /// <summary>Attaches an image (pasted, dropped, or converted to PNG by the view).</summary>
    public void AddImage(string name, string mediaType, byte[] data, string? path = null)
    {
        if (data.Length > MaxImageBytes)
        {
            Notice = $"Not attached: {name} is larger than {MaxImageBytes / 1024 / 1024} MB.";
            return;
        }

        Attachments.Add(new AgentAttachment(AttachmentKind.Image, name) { Data = data, MediaType = mediaType, Path = path });
    }

    /// <summary>A name for the next pasted image ("pasted-image-1.png").</summary>
    public string NextPastedImageName() => $"pasted-image-{++_pasteCount}.png";

    /// <summary>Text dropped on the chat goes into the input.</summary>
    public void InsertText(string text)
    {
        Input = Input.Length == 0 ? text : Input.TrimEnd() + (Input.EndsWith('\n') ? string.Empty : "\n") + text;
    }

    private static bool LooksBinary(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[8192];
            var read = stream.Read(buffer, 0, buffer.Length);
            return Array.IndexOf(buffer, (byte)0, 0, read) >= 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false; // reading it when sending reports the problem
        }
    }

    /// <summary>The user's message for the model: the text, each attached file's content in a &lt;file&gt; tag, folders as listings, images as images.</summary>
    private async Task<ChatMessage> BuildMessageAsync(string text, IReadOnlyList<AgentAttachment> attachments, ModelInfo? model)
    {
        var content = new StringBuilder(text);
        var images = new List<ChatImage>();
        foreach (var attachment in attachments)
        {
            var shown = attachment.Path is { } path ? DisplayPath(path) : attachment.Name;
            switch (attachment.Kind)
            {
                case AttachmentKind.Image when attachment.Data is { } data:
                    if (model?.SupportsVision == false)
                    {
                        AddNotice($"{model.DisplayName} can't see images, so {attachment.Name} was left out. Pick a vision model to send images.", isError: false);
                    }
                    else
                    {
                        images.Add(new ChatImage(attachment.MediaType ?? "image/png", data));
                        content.Append($"\n\n(Attached image: {shown})");
                    }

                    break;
                case AttachmentKind.Folder when attachment.Path is { } folder:
                    content.Append($"\n\n<folder path=\"{shown}\">\n{FolderListing(folder)}</folder>");
                    break;
                case AttachmentKind.File when attachment.Path is { } file:
                    content.Append($"\n\n<file path=\"{shown}\">\n{await ReadAttachmentAsync(file)}\n</file>");
                    break;
            }
        }

        return ChatMessage.User(content.ToString().Trim(), images);
    }

    private async Task<string> ReadAttachmentAsync(string path)
    {
        string text;
        if (_workspace.Editors.FindTab(path) is { } tab)
        {
            text = tab.Document.GetText(); // unsaved changes included
        }
        else
        {
            try
            {
                text = LineEndings.Normalize((await _workspace.FileSystem.ReadTextAsync(path)).Text);
            }
            catch (BinaryFileException)
            {
                return "(a binary file; its content was not included)";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return $"(could not be read: {exception.Message})";
            }
        }

        return text.Length <= MaxAttachmentChars ? text : text[..MaxAttachmentChars] + $"\n… (cut: the file has {text.Length:N0} characters)";
    }

    private string FolderListing(string folder)
    {
        var listing = new StringBuilder();
        var count = 0;
        try
        {
            foreach (var file in _workspace.FileSystem.EnumerateFiles(folder))
            {
                if (++count > AgentToolbox.MaxListed)
                {
                    listing.Append("… (more files not listed)\n");
                    break;
                }

                listing.Append(Path.GetRelativePath(folder, file).Replace('\\', '/')).Append('\n');
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            listing.Append($"(could not be listed: {exception.Message})\n");
        }

        return listing.ToString();
    }

    /// <summary>A path relative to the open folder when it is inside it (what the agent's tools use), else as it is.</summary>
    private string DisplayPath(string path)
    {
        if (_workspace.Root() is { } root)
        {
            var relative = Path.GetRelativePath(root, path);
            if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
            {
                return relative.Replace('\\', '/');
            }
        }

        return path;
    }
}
