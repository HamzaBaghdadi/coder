using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Coder.Core.Extensions;

/// <summary>
/// Coder's extensions folder: what is installed, installing and uninstalling. Each extension lives in
/// <c>&lt;folder&gt;/&lt;publisher.name-version&gt;</c> in VS Code's layout (package.json at its root), so
/// the theme catalog reads it like VS Code's own extensions.
/// <para>
/// Safe by construction: packages are checked against their SHA-256 when the source publishes one,
/// size-limited, extracted into a staging folder (no entry may escape it) and only then moved into
/// place. The store never runs anything: data extensions (themes, snippets) are only read, and an
/// extension's program (<c>coder.program</c>) is started by the extension host, and only with the
/// user's consent.
/// </para>
/// </summary>
public sealed class ExtensionStore(string folder, HttpClient http)
{
    public const long MaxPackageBytes = 64L * 1024 * 1024;

    /// <summary>Written next to package.json: the source the extension came from.</summary>
    private const string SourceFile = ".coder-source";

    /// <summary>Written next to package.json: the SHA-256 of the package it was installed from.</summary>
    private const string Sha256File = ".coder-sha256";
    private static readonly JsonDocumentOptions JsonOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public string Folder => folder;

    /// <summary>Raised after an install or uninstall, so views and the theme list refresh.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Called with an extension's id before its files are replaced or deleted, so its running program can
    /// be stopped first (Windows won't delete a running program).
    /// </summary>
    public Func<string, Task>? BeforeRemoveAsync { get; set; }

    /// <summary>Installed extensions, newest version of each.</summary>
    public IReadOnlyList<InstalledExtension> Installed()
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var found = new Dictionary<string, InstalledExtension>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in Directory.EnumerateDirectories(folder).Where(d => !Path.GetFileName(d).StartsWith('.')))
        {
            if (Read(directory) is { } extension
                && (!found.TryGetValue(extension.Id, out var other) || CompareVersions(extension.Version, other.Version) > 0))
            {
                found[extension.Id] = extension;
            }
        }

        return found.Values.OrderBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public InstalledExtension? Find(string id) => Installed().FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Downloads, verifies and installs; an older version of the same extension is replaced.</summary>
    /// <exception cref="ExtensionException">Anything that went wrong, said plainly.</exception>
    public async Task<InstalledExtension> InstallAsync(ExtensionListing listing, CancellationToken cancellationToken = default)
    {
        var package = Path.Combine(Path.GetTempPath(), $"coder-extension-{Guid.NewGuid():N}.zip");
        try
        {
            await DownloadAsync(listing.DownloadUrl, package, cancellationToken);
            var expected = listing.Sha256 ?? (listing.Sha256Url is { } url ? await ReadHashAsync(url, cancellationToken) : null);
            string actual;
            await using (var stream = File.OpenRead(package))
            {
                actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            }

            if (expected is not null && !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new ExtensionException($"{listing.DisplayName} wasn't installed: the download doesn't match what {listing.Source} published (its checksum differs).");
            }

            if (BeforeRemoveAsync is { } beforeRemove && Find(listing.Id) is not null)
            {
                await beforeRemove(listing.Id); // an update replaces the running version
            }

            var installed = Install(package, listing, actual);
            Changed?.Invoke(this, EventArgs.Empty);
            return installed;
        }
        finally
        {
            TryDelete(package);
        }
    }

    /// <summary>Removes every installed version of the extension.</summary>
    public void Uninstall(string id)
    {
        RemoveVersions(id);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stops the extension's program, if it runs one, then removes it.</summary>
    public async Task UninstallAsync(string id)
    {
        if (BeforeRemoveAsync is { } beforeRemove)
        {
            await beforeRemove(id);
        }

        Uninstall(id);
    }

    /// <summary>Deletes the installed folders of an extension (never a staging folder).</summary>
    private void RemoveVersions(string id)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(folder).Where(d => !Path.GetFileName(d).StartsWith('.')).ToList())
        {
            if (Read(directory) is { } extension && string.Equals(extension.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>Unpacks a package (a .vsix or a plain zip) into place. Internal for tests.</summary>
    internal InstalledExtension Install(string package, ExtensionListing listing, string? sha256 = null)
    {
        Directory.CreateDirectory(folder);
        var staging = Path.Combine(folder, $".staging-{Guid.NewGuid():N}");
        try
        {
            Extract(package, staging);
            var extension = Read(staging) ?? throw new ExtensionException($"{listing.DisplayName} isn't an extension Coder can read (it has no package.json).");
            if (!string.Equals(extension.Id, listing.Id, StringComparison.OrdinalIgnoreCase))
            {
                throw new ExtensionException($"{listing.DisplayName} wasn't installed: the package is {extension.Id}, not {listing.Id}.");
            }

            File.WriteAllText(Path.Combine(staging, SourceFile), listing.Source); // so Settings Sync knows where to find it again
            if (sha256 is not null)
            {
                File.WriteAllText(Path.Combine(staging, Sha256File), sha256); // consent to run a program is for this exact package
            }

            RemoveVersions(extension.Id); // one version at a time
            var target = Path.Combine(folder, $"{extension.Id}-{extension.Version}");
            Directory.Move(staging, target);
            var installed = Read(target) ?? extension with { Folder = target, Source = listing.Source };
            MakeExecutable(installed.ProgramPath);
            return installed;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new ExtensionException($"{listing.DisplayName} couldn't be installed: {exception.Message}", exception);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    /// <summary>A .vsix keeps the extension under <c>extension/</c>; a plain zip has it at the root.</summary>
    private static void Extract(string package, string destination)
    {
        using var zip = ZipFile.OpenRead(package);
        var prefix = zip.Entries.Any(e => e.FullName.Equals("extension/package.json", StringComparison.OrdinalIgnoreCase)) ? "extension/" : string.Empty;
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || name.EndsWith('/'))
            {
                continue;
            }

            var path = Path.GetFullPath(Path.Combine(destination, name[prefix.Length..]));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"the package tries to write outside its folder ({entry.FullName}).");
            }

            total += entry.Length;
            if (total > MaxPackageBytes * 4)
            {
                throw new InvalidDataException("the package unpacks to more than 256 MB.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, overwrite: true);
            var mode = (UnixFileMode)((entry.ExternalAttributes >> 16) & 0x1FF); // a zip made on unix keeps permissions here
            if (!OperatingSystem.IsWindows() && mode != UnixFileMode.None)
            {
                File.SetUnixFileMode(path, mode);
            }
        }
    }

    /// <summary>A program packed on Windows has no unix permissions in the zip: make it executable.</summary>
    private static void MakeExecutable(string? program)
    {
        if (program is not null && !OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(program, File.GetUnixFileMode(program) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
    }

    private async Task DownloadAsync(string address, string destination, CancellationToken cancellationToken)
    {
        try
        {
            if (!address.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(address, destination, overwrite: true); // a local registry (tests, offline mirrors)
                return;
            }

            using var response = await http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxPackageBytes)
            {
                throw new ExtensionException("The package is larger than 64 MB, which is more than an extension Coder can use needs.");
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = File.Create(destination);
            var buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                written += read;
                if (written > MaxPackageBytes)
                {
                    throw new ExtensionException("The package is larger than 64 MB, which is more than an extension Coder can use needs.");
                }

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new ExtensionException("The extension couldn't be downloaded. Check your connection and try again.", exception);
        }
    }

    /// <summary>Open VSX's .sha256 file: the hex digest, possibly followed by the file name.</summary>
    private async Task<string?> ReadHashAsync(string address, CancellationToken cancellationToken)
    {
        try
        {
            var text = await http.GetStringAsync(address, cancellationToken);
            var hash = text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return hash is { Length: 64 } && hash.All(Uri.IsHexDigit) ? hash : null;
        }
        catch (HttpRequestException exception)
        {
            throw new ExtensionException("The extension's checksum couldn't be downloaded, so it wasn't installed.", exception);
        }
    }

    /// <summary>An extension folder's package.json, or null when it has none (or an unreadable one).</summary>
    private static InstalledExtension? Read(string directory)
    {
        var manifest = Path.Combine(directory, "package.json");
        if (!File.Exists(manifest))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest), JsonOptions);
            var root = document.RootElement;
            var publisher = String(root, "publisher");
            var name = String(root, "name");
            if (publisher is null || name is null)
            {
                return null;
            }

            var themes = root.TryGetProperty("contributes", out var contributes) && contributes.TryGetProperty("themes", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(t => String(t, "label") ?? String(t, "id")).OfType<string>().ToList()
                : [];
            var snippets = contributes.ValueKind == JsonValueKind.Object && contributes.TryGetProperty("snippets", out var snippetList) && snippetList.ValueKind == JsonValueKind.Array
                ? snippetList.EnumerateArray().Where(s => String(s, "path") is not null).Select(s => String(s, "language") ?? "*").Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : [];
            var display = String(root, "displayName");
            var views = root.TryGetProperty("coder", out var coderSection) && coderSection.ValueKind == JsonValueKind.Object
                        && coderSection.TryGetProperty("views", out var viewList) && viewList.ValueKind == JsonValueKind.Array
                ? viewList.EnumerateArray()
                    .Where(v => String(v, "id") is { Length: > 0 } && String(v, "title") is { Length: > 0 } && (String(v, "location") ?? "secondarySidebar") == "secondarySidebar")
                    .Select(v => new ExtensionView(String(v, "id")!, String(v, "title")!, String(v, "command")))
                    .ToList()
                : [];
            var programs = root.TryGetProperty("coder", out var coder) && coder.ValueKind == JsonValueKind.Object
                           && coder.TryGetProperty("program", out var program) && program.ValueKind == JsonValueKind.Object
                ? program.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.OrdinalIgnoreCase)
                : [];
            var ownProgram = programs.TryGetValue(Platform.RuntimeId, out var ownRelative) ? Inside(directory, ownRelative) : null;
            var agents = root.TryGetProperty("coder", out var agentSection) && agentSection.ValueKind == JsonValueKind.Object
                         && agentSection.TryGetProperty("agents", out var agentList) && agentList.ValueKind == JsonValueKind.Array
                ? agentList.EnumerateArray()
                    .Where(a => String(a, "id") is { Length: > 0 } && String(a, "name") is { Length: > 0 })
                    .Select(a => new ExtensionAgent(
                        String(a, "id")!,
                        String(a, "name")!,
                        a.TryGetProperty("program", out var agentProgram) && agentProgram.ValueKind == JsonValueKind.Object
                            ? agentProgram.TryGetProperty(Platform.RuntimeId, out var path) && path.ValueKind == JsonValueKind.String ? Inside(directory, path.GetString()!) : null
                            : ownProgram,
                        a.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array
                            ? [.. args.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)]
                            : []))
                    .ToList()
                : [];
            return new InstalledExtension(
                $"{publisher}.{name}",
                display is null || display.StartsWith('%') ? name : display,
                publisher,
                String(root, "description") is { } description && !description.StartsWith('%') ? description : string.Empty,
                String(root, "version") ?? "0.0.0",
                directory,
                themes)
            {
                IconPath = String(root, "icon") is { } icon && File.Exists(Path.Combine(directory, icon)) ? Path.GetFullPath(Path.Combine(directory, icon)) : null,
                SnippetLanguages = snippets,
                DeclaresProgram = programs.Count > 0,
                ProgramPath = programs.TryGetValue(Platform.RuntimeId, out var relative) ? Inside(directory, relative) : null,
                Commands = Commands(contributes, display is null || display.StartsWith('%') ? name : display),
                Views = views,
                Agents = agents,
                Source = TextFile(directory, SourceFile),
                PackageSha256 = TextFile(directory, Sha256File),
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? TextFile(string directory, string name) =>
        File.Exists(Path.Combine(directory, name)) && File.ReadAllText(Path.Combine(directory, name)).Trim() is { Length: > 0 } text ? text : null;

    /// <summary>A path the manifest names, when it is a file inside the extension's folder.</summary>
    private static string? Inside(string directory, string relative)
    {
        var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(directory, relative));
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(path) ? path : null;
    }

    /// <summary>
    /// VS Code's <c>contributes.commands</c> (<c>{ command, title, category }</c>) with the keys from
    /// <c>contributes.keybindings</c> (<c>{ command, key: "ctrl+shift+k" }</c>) in Coder's gesture text.
    /// </summary>
    private static List<ExtensionCommand> Commands(JsonElement contributes, string extensionName)
    {
        if (contributes.ValueKind != JsonValueKind.Object || !contributes.TryGetProperty("commands", out var commands) || commands.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        if (contributes.TryGetProperty("keybindings", out var bindings) && bindings.ValueKind == JsonValueKind.Array)
        {
            foreach (var binding in bindings.EnumerateArray())
            {
                if (String(binding, "command") is { } command && (String(binding, OperatingSystem.IsMacOS() ? "mac" : "key") ?? String(binding, "key")) is { } key)
                {
                    keys.TryAdd(command, Gesture(key));
                }
            }
        }

        return commands.EnumerateArray()
            .Select(c => (Id: String(c, "command"), Title: String(c, "title"), Category: String(c, "category")))
            .Where(c => c.Id is { Length: > 0 } && c.Title is { Length: > 0 })
            .Select(c => new ExtensionCommand(c.Id!, c.Category is { Length: > 0 } category ? category : extensionName, c.Title!, keys.GetValueOrDefault(c.Id!)))
            .ToList();
    }

    /// <summary>"ctrl+k ctrl+t" → "Ctrl+K Ctrl+T", "ctrl+escape" → "Ctrl+Escape".</summary>
    internal static string Gesture(string key) =>
        string.Join(' ', key.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(chord =>
            string.Join('+', chord.Split('+').Select(part => part.ToLowerInvariant() switch
            {
                "ctrl" => "Ctrl",
                "shift" => "Shift",
                "alt" => "Alt",
                "cmd" or "meta" or "win" => "Meta",
                var other when other.Length == 1 => other.ToUpperInvariant(),
                var other => char.ToUpperInvariant(other[0]) + other[1..],
            }))));

    /// <summary>Compares "1.10.0" and "1.9.2" as versions (pre-release suffixes ignored).</summary>
    internal static int CompareVersions(string a, string b) =>
        Version.TryParse(a.Split('-')[0], out var x) && Version.TryParse(b.Split('-')[0], out var y) ? x.CompareTo(y) : string.CompareOrdinal(a, b);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // a temp file; Windows cleans those up
        }
    }
}
