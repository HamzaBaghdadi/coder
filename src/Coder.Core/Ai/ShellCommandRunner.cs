using System.Diagnostics;
using System.Text;

namespace Coder.Core.Ai;

/// <summary>
/// Runs a command line in a shell (PowerShell on Windows, preferring PowerShell 7; /bin/sh elsewhere),
/// without a window and without input (a command that waits for input gets end-of-file), capturing
/// what it prints. A timeout or cancellation kills the whole process tree.
/// </summary>
public sealed class ShellCommandRunner : ICommandRunner
{
    /// <summary>Only the end of a long output is kept (that is where errors are).</summary>
    private const int MaxOutputChars = 200_000;

    public async Task<CommandResult> RunAsync(string command, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (OperatingSystem.IsWindows())
        {
            start.FileName = FindOnPath("pwsh.exe") ?? "powershell.exe";
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");

            // UTF-8 output, so non-ASCII text survives.
            start.ArgumentList.Add("[Console]::OutputEncoding = [Text.Encoding]::UTF8; " + command);
        }
        else
        {
            start.FileName = "/bin/sh";
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(command);
        }

        start.Environment["NO_COLOR"] = "1";
        start.Environment["TERM"] = "dumb";
        start.Environment["GIT_PAGER"] = "cat";
        start.Environment["PAGER"] = "cat";

        var output = new StringBuilder();
        void Append(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (output)
            {
                output.Append(line).Append('\n');
                if (output.Length > MaxOutputChars * 2)
                {
                    output.Remove(0, output.Length - MaxOutputChars);
                }
            }
        }

        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            return new CommandResult(null, $"Could not start {start.FileName}: {exception.Message}", false);
        }

        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timer.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // it ended meanwhile
            }

            if (!timedOut)
            {
                throw;
            }
        }

        if (!timedOut)
        {
            process.WaitForExit(); // the last lines of output
        }

        string text;
        lock (output)
        {
            text = output.ToString().TrimEnd();
        }

        if (text.Length > MaxOutputChars)
        {
            text = text[^MaxOutputChars..];
        }

        return new CommandResult(timedOut ? null : process.ExitCode, text, timedOut);
    }

    private static string? FindOnPath(string file) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => Path.Combine(folder.Trim(), file))
            .FirstOrDefault(File.Exists);
}
