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
/// place. Nothing in a package is ever run.
/// </para>
/// </summary>
public sealed class ExtensionStore(string folder, HttpClient http)
{
    public const long MaxPackageBytes = 64L * 1024 * 1024;
    private static readonly JsonDocumentOptions JsonOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public string Folder => folder;

    /// <summary>Raised after an install or uninstall, so views and the theme list refresh.</summary>
    public event EventHandler? Changed;

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
            if (expected is not null)
            {
                await using var stream = File.OpenRead(package);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ExtensionException($"{listing.DisplayName} wasn't installed: the download doesn't match what {listing.Source} published (its checksum differs).");
                }
            }

            var installed = Install(package, listing);
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
    internal InstalledExtension Install(string package, ExtensionListing listing)
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

            RemoveVersions(extension.Id); // one version at a time
            var target = Path.Combine(folder, $"{extension.Id}-{extension.Version}");
            Directory.Move(staging, target);
            return extension with { Folder = target };
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
            var display = String(root, "displayName");
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
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

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
