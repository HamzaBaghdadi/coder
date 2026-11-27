using System.ComponentModel;
using System.Diagnostics;

namespace Coder.Core.Lsp;

/// <summary>
/// A child process's pipes, for JSON-RPC over stdio (language servers, extension programs). stderr is
/// drained into the last few lines, for error messages. Disposing it ends the process (and anything it
/// started) if it has not exited.
/// </summary>
public sealed class ProcessTransport : ILanguageServerTransport
{
    private const int KeptErrorLines = 20;

    private readonly Process _process;
    private readonly Queue<string> _errorLines = new();

    public ProcessTransport(Process process)
    {
        _process = process;

        // stderr must be drained or a chatty child blocks on a full pipe.
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (_errorLines)
            {
                _errorLines.Enqueue(e.Data);
                if (_errorLines.Count > KeptErrorLines)
                {
                    _errorLines.Dequeue();
                }
            }
        };
        process.BeginErrorReadLine();
    }

    /// <summary>Starts a program with redirected stdio and no window.</summary>
    /// <param name="environment">Variables added to (or replacing) Coder's own; a null value removes one.</param>
    /// <exception cref="Win32Exception">It couldn't be started.</exception>
    public static ProcessTransport Start(string executable, IEnumerable<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string?>? environment = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment ?? new Dictionary<string, string?>())
        {
            start.Environment[name] = value;
        }

        return new ProcessTransport(Process.Start(start) ?? throw new Win32Exception($"Could not start {executable}."));
    }

    public Stream Input => _process.StandardOutput.BaseStream;

    public Stream Output => _process.StandardInput.BaseStream;

    public int ProcessId => _process.Id;

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public string RecentErrorOutput
    {
        get
        {
            lock (_errorLines)
            {
                return string.Join(Environment.NewLine, _errorLines);
            }
        }
    }

    public void Dispose()
    {
        try
        {
            if (!_process.WaitForExit(500))
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(2000); // so its files (the program itself) are released before an uninstall deletes them
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // Already gone.
        }

        _process.Dispose();
    }
}
