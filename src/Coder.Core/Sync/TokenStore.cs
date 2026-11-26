using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Coder.Core.Sync;

/// <summary>The signed-in GitHub account and its token.</summary>
public sealed record GitHubSignIn(string Login, string Token);

/// <summary>Keeps the GitHub token between sessions, never in settings.json.</summary>
public interface ITokenStore
{
    GitHubSignIn? Load();

    void Save(GitHubSignIn signIn);

    /// <summary>Forgets the token (signing out).</summary>
    void Delete();
}

/// <summary>
/// The token encrypted with Windows DPAPI for the current Windows user (<see cref="ProtectedData"/>), in a
/// file of its own in the user data folder. Another Windows user, or a copy of the file on another
/// machine, can't decrypt it. Off Windows nothing is stored: the user signs in each session.
/// </summary>
public sealed class DpapiTokenStore(string path) : ITokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Coder.SettingsSync.GitHub");

    public string FilePath => path;

    public GitHubSignIn? Load()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            var stored = JsonSerializer.Deserialize(json, SyncJson.Default.StoredToken);
            return stored is { Login.Length: > 0, Token.Length: > 0 } ? new GitHubSignIn(stored.Login, stored.Token) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            return null; // unreadable (another user's, or damaged): signing in again replaces it
        }
    }

    public void Save(GitHubSignIn signIn)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(new StoredToken { Login = signIn.Login, Token = signIn.Token }, SyncJson.Default.StoredToken);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser));
        File.Move(temp, path, overwrite: true);
    }

    public void Delete()
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // signing in again overwrites it
        }
    }
}

/// <summary>Keeps the token in memory only (tests, and platforms without DPAPI).</summary>
public sealed class MemoryTokenStore : ITokenStore
{
    private GitHubSignIn? _signIn;

    public GitHubSignIn? Load() => _signIn;

    public void Save(GitHubSignIn signIn) => _signIn = signIn;

    public void Delete() => _signIn = null;
}

internal sealed class StoredToken
{
    public string Login { get; set; } = string.Empty;

    public string Token { get; set; } = string.Empty;
}
