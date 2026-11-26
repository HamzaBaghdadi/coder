namespace Coder.Core.Terminal;

/// <summary>A shell the terminal can start: its stable id (for settings), name and command line.</summary>
public sealed record ShellProfile(string Id, string Name, string CommandLine);

/// <summary>
/// The shells installed on this machine, like VS Code's terminal profiles: PowerShell 7, Windows
/// PowerShell, Command Prompt, Git Bash and WSL on Windows. Only shells that are really there are listed.
/// </summary>
public static class ShellProfiles
{
    /// <summary>The installed shells, PowerShell first (the default when no other is chosen).</summary>
    public static IReadOnlyList<ShellProfile> Detect() =>
        OperatingSystem.IsWindows() ? DetectWindows(Environment.GetEnvironmentVariable("PATH") ?? string.Empty) : [];

    /// <summary>The chosen profile when it is installed, else the first (PowerShell).</summary>
    public static ShellProfile? Resolve(IReadOnlyList<ShellProfile> profiles, string? id) =>
        profiles.FirstOrDefault(p => p.Id == id) ?? (profiles.Count > 0 ? profiles[0] : null);

    internal static IReadOnlyList<ShellProfile> DetectWindows(string path)
    {
        var folders = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var profiles = new List<ShellProfile>();

        if ((OnPath(folders, "pwsh.exe") ?? Existing(Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe"))) is { } pwsh)
        {
            profiles.Add(new ShellProfile("pwsh", "PowerShell", $"{Quote(pwsh)} -NoLogo"));
        }

        if ((Existing(Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe")) ?? OnPath(folders, "powershell.exe")) is { } powershell)
        {
            profiles.Add(new ShellProfile("powershell", "Windows PowerShell", $"{Quote(powershell)} -NoLogo"));
        }

        if (Existing(Path.Combine(system, "cmd.exe")) is { } cmd)
        {
            profiles.Add(new ShellProfile("cmd", "Command Prompt", Quote(cmd)));
        }

        // Git Bash: from Git's usual folder, or next to the git on PATH (<git>\cmd\git.exe → <git>\bin\bash.exe).
        var gitOnPath = OnPath(folders, "git.exe");
        var gitRoot = gitOnPath is null ? null : Path.GetDirectoryName(Path.GetDirectoryName(gitOnPath));
        if ((Existing(Path.Combine(programFiles, "Git", "bin", "bash.exe"))
             ?? (gitRoot is null ? null : Existing(Path.Combine(gitRoot, "bin", "bash.exe")))) is { } bash)
        {
            profiles.Add(new ShellProfile("git-bash", "Git Bash", $"{Quote(bash)} --login -i"));
        }

        if (Existing(Path.Combine(system, "wsl.exe")) is { } wsl)
        {
            profiles.Add(new ShellProfile("wsl", "WSL", Quote(wsl)));
        }

        return profiles;
    }

    private static string? OnPath(IEnumerable<string> folders, string file) =>
        folders.Select(folder => Path.Combine(folder, file)).FirstOrDefault(File.Exists);

    private static string? Existing(string path) => File.Exists(path) ? path : null;

    private static string Quote(string path) => path.Contains(' ', StringComparison.Ordinal) ? $"\"{path}\"" : path;
}
