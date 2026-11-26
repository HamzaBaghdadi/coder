namespace Coder.Core.Terminal;

/// <summary>
/// Shell integration: a small script the shell runs at start so it reports, with OSC 633 sequences, where
/// each prompt starts, each command's exit status and the current folder. The user's own prompt (from
/// their profile, oh-my-posh…) is kept: the script wraps it. PowerShell is supported for now.
/// </summary>
public static class ShellIntegration
{
    /// <summary>
    /// Wraps the prompt function. A command ran when the history grew since the last prompt; then its
    /// status is reported (0 on success, else the exit code a program set, else 1), and the next prompt
    /// is marked. <c>$?</c> must be read first: anything the prompt does changes it.
    /// </summary>
    public const string PowerShellScript = """
        if ($global:__CoderShellIntegration) { return }
        $global:__CoderShellIntegration = $true
        $global:__CoderPrompt = $function:prompt
        $global:__CoderLastHistoryId = -1

        function global:prompt {
            $succeeded = $?
            $exitCode = $global:LASTEXITCODE
            $esc = [char]27
            $bel = [char]7
            $out = ''

            $history = Get-History -Count 1
            $historyId = if ($history) { $history.Id } else { 0 }
            if ($global:__CoderLastHistoryId -ge 0 -and $historyId -ne $global:__CoderLastHistoryId) {
                $status = if ($succeeded) { 0 } elseif ($exitCode -is [int] -and $exitCode -ne 0) { $exitCode } else { 1 }
                $out += "$esc]633;D;$status$bel"
            }
            $global:__CoderLastHistoryId = $historyId

            $location = $executionContext.SessionState.Path.CurrentLocation
            if ($location.Provider.Name -eq 'FileSystem') {
                $out += "$esc]633;P;Cwd=$($location.ProviderPath)$bel"
            }

            $out += "$esc]633;A$bel"
            $out += & $global:__CoderPrompt
            $out += "$esc]633;B$bel"
            $global:LASTEXITCODE = $exitCode
            return $out
        }
        """;

    /// <summary>The script's file in the temp folder, (re)written when missing or out of date.</summary>
    public static string PowerShellScriptPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "coder-shell-integration.ps1");
        try
        {
            if (!File.Exists(path) || File.ReadAllText(path) != PowerShellScript)
            {
                File.WriteAllText(path, PowerShellScript);
            }
        }
        catch (IOException)
        {
            // Another terminal is writing the same file right now: it ends up the same.
        }

        return path;
    }

    /// <summary>
    /// The command line that starts <paramref name="profile"/> with shell integration, or its own command
    /// line when the shell isn't supported. The profile (and the user's prompt) loads first; the script
    /// then wraps the prompt. A failing script never stops the shell.
    /// </summary>
    public static string CommandLine(ShellProfile profile) => profile.Id is "pwsh" or "powershell"
        ? $"{profile.CommandLine} -NoExit -Command \"try {{ . '{PowerShellScriptPath()}' }} catch {{ }}\""
        : profile.CommandLine;
}
