using System.Text.Json;

namespace Coder.Core.Extensions;

/// <summary>
/// Which extension programs the user allowed (or refused) to run on this machine, in
/// <c>&lt;extensions&gt;/.trust.json</c>. It is not synced: an extension that Settings Sync installs asks
/// again here. A decision is for one package (its SHA-256), so an update asks again.
/// </summary>
public sealed class ExtensionTrust(string file)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly Lock _gate = new();

    public enum Decision
    {
        Unknown,
        Allowed,
        Denied,
    }

    public Decision For(InstalledExtension extension)
    {
        lock (_gate)
        {
            return Load().TryGetValue(extension.Id, out var entry) && entry.Package == Key(extension)
                ? entry.Allowed ? Decision.Allowed : Decision.Denied
                : Decision.Unknown;
        }
    }

    public void Set(InstalledExtension extension, bool allowed)
    {
        lock (_gate)
        {
            var all = Load();
            all[extension.Id] = new Entry(Key(extension), allowed);
            Save(all);
        }
    }

    /// <summary>Forgets the decision, so the next start asks again (after an uninstall).</summary>
    public void Forget(string id)
    {
        lock (_gate)
        {
            var all = Load();
            if (all.Remove(id))
            {
                Save(all);
            }
        }
    }

    /// <summary>The package's SHA-256, or its version when Coder didn't record one (copied in by hand).</summary>
    private static string Key(InstalledExtension extension) => extension.PackageSha256 ?? $"version:{extension.Version}";

    private Dictionary<string, Entry> Load()
    {
        try
        {
            return File.Exists(file)
                ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(file), Options) is { } read
                    ? new Dictionary<string, Entry>(read, StringComparer.OrdinalIgnoreCase)
                    : new(StringComparer.OrdinalIgnoreCase)
                : new(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(StringComparer.OrdinalIgnoreCase); // unreadable: ask again rather than run unasked
        }
    }

    private void Save(Dictionary<string, Entry> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(all, Options));
        File.Move(temporary, file, overwrite: true);
    }

    private sealed record Entry(string Package, bool Allowed);
}
