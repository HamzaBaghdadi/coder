namespace Coder.Presentation.Services;

/// <summary>The <c>coder</c> terminal command and File Explorer's "Open with Coder"; implemented by the app (Windows).</summary>
public interface IShellIntegration
{
    bool IsInstalled { get; }

    /// <summary>Puts <c>coder</c> on the user's PATH and adds "Open with Coder"; returns what to tell the user.</summary>
    /// <exception cref="IOException">The scripts couldn't be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The registry or the folder refused.</exception>
    string Install();

    /// <summary>Takes both away again; returns what to tell the user.</summary>
    string Uninstall();
}
