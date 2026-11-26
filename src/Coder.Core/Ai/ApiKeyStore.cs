using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Coder.Core.Ai;

/// <summary>Keeps the providers' API keys, by provider id, never in settings.json.</summary>
public interface IApiKeyStore
{
    string? Get(string providerId);

    /// <summary>Stores the key; null or empty forgets it.</summary>
    void Set(string providerId, string? key);
}

/// <summary>
/// The keys encrypted with Windows DPAPI for the current Windows user (<see cref="ProtectedData"/>), in one
/// file of their own next to settings.json, like Settings Sync's GitHub token. Another Windows user, or a
/// copy of the file on another machine, can't decrypt them. Off Windows nothing is written: keys last
/// for the session only.
/// </summary>
public sealed class DpapiApiKeyStore(string path) : IApiKeyStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Coder.Agent.ApiKeys");
    private readonly object _gate = new();
    private Dictionary<string, string>? _keys;

    public string FilePath => path;

    public string? Get(string providerId)
    {
        lock (_gate)
        {
            return Keys().TryGetValue(providerId, out var key) ? key : null;
        }
    }

    public void Set(string providerId, string? key)
    {
        lock (_gate)
        {
            var keys = Keys();
            if (string.IsNullOrWhiteSpace(key))
            {
                if (!keys.Remove(providerId))
                {
                    return;
                }
            }
            else
            {
                keys[providerId] = key.Trim();
            }

            Write(keys);
        }
    }

    private Dictionary<string, string> Keys()
    {
        if (_keys is not null)
        {
            return _keys;
        }

        _keys = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return _keys;
        }

        try
        {
            var json = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            if (JsonNode.Parse(json) is JsonObject stored)
            {
                foreach (var (id, value) in stored)
                {
                    if (value?.GetValueKind() == JsonValueKind.String && value.GetValue<string>() is { Length: > 0 } key)
                    {
                        _keys[id] = key;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            // Unreadable (another user's, or damaged): entering a key again replaces the file.
        }

        return _keys;
    }

    private void Write(Dictionary<string, string> keys)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var json = new JsonObject();
        foreach (var (id, key) in keys)
        {
            json[id] = key;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, ProtectedData.Protect(Encoding.UTF8.GetBytes(json.ToJsonString()), Entropy, DataProtectionScope.CurrentUser));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            // The key still works for this session.
        }
    }
}

/// <summary>Keys in memory only (tests, and platforms without DPAPI).</summary>
public sealed class MemoryApiKeyStore : IApiKeyStore
{
    private readonly Dictionary<string, string> _keys = new(StringComparer.Ordinal);

    public string? Get(string providerId) => _keys.TryGetValue(providerId, out var key) ? key : null;

    public void Set(string providerId, string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            _keys.Remove(providerId);
        }
        else
        {
            _keys[providerId] = key.Trim();
        }
    }
}
