using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Coder.App.Terminal;

/// <summary>
/// A shell attached to a Windows pseudo-console (ConPTY, the same API Windows Terminal uses).
/// <para>
/// Two pipes connect us to the pseudo-console: we write keystrokes into one and read VT-encoded
/// output from the other on a background thread. The pseudo-console translates between that byte
/// stream and the console the shell sees.
/// </para>
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
public sealed class WindowsPseudoTerminal : IPseudoTerminal
{
    private readonly IntPtr _pseudoConsole;
    private readonly SafeFileHandle _processHandle;
    private readonly FileStream _input;
    private readonly FileStream _output;
    private readonly Thread _readThread;
    private int _disposed;

    public WindowsPseudoTerminal(string commandLine, string workingDirectory, int columns, int rows)
    {
        // Pipes: we write to inputWrite (the console reads inputRead); the console writes outputWrite (we read outputRead).
        Check(NativeMethods.CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0));
        Check(NativeMethods.CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0));

        var result = NativeMethods.CreatePseudoConsole(Size(columns, rows), inputRead, outputWrite, 0, out _pseudoConsole);
        if (result != 0)
        {
            throw new Win32Exception(result, "Could not create the pseudo-console.");
        }

        // The pseudo-console now owns its ends of the pipes.
        inputRead.Dispose();
        outputWrite.Dispose();

        _processHandle = StartProcess(commandLine, workingDirectory, _pseudoConsole);
        _input = new FileStream(inputWrite, FileAccess.Write);
        _output = new FileStream(outputRead, FileAccess.Read);

        _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "Terminal output" };
        _readThread.Start();
        new Thread(WaitForExit) { IsBackground = true, Name = "Terminal exit" }.Start();
    }

    public event EventHandler<string>? Output;

    public event EventHandler<int>? Exited;

    public void Write(string text)
    {
        if (_disposed != 0)
        {
            return;
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            _input.Write(bytes);
            _input.Flush();
        }
        catch (IOException)
        {
            // The shell has gone away; the exit event reports it.
        }
    }

    public void Resize(int columns, int rows)
    {
        if (_disposed == 0)
        {
            _ = NativeMethods.ResizePseudoConsole(_pseudoConsole, Size(columns, rows));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Closing the pseudo-console ends the shell and makes the output pipe report end-of-file.
        // The process handle is not closed here: the exit thread may be using it, and closes it itself.
        NativeMethods.ClosePseudoConsole(_pseudoConsole);
        _input.Dispose();
        _output.Dispose();
    }

    private void ReadLoop()
    {
        var buffer = new byte[16 * 1024];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        var decoder = Encoding.UTF8.GetDecoder(); // keeps UTF-8 sequences split across reads intact
        try
        {
            int read;
            while ((read = _output.Read(buffer, 0, buffer.Length)) > 0)
            {
                var count = decoder.GetChars(buffer, 0, read, chars, 0);
                Output?.Invoke(this, new string(chars, 0, count));
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The pipe closed: the terminal is shutting down.
        }
    }

    /// <summary>Owns the process handle: waits for the shell to end, reports how (unless it was killed), then closes the handle.</summary>
    private void WaitForExit()
    {
        using (_processHandle)
        {
            _ = NativeMethods.WaitForSingleObject(_processHandle, NativeMethods.Infinite);
            _ = NativeMethods.GetExitCodeProcess(_processHandle, out var exitCode);
            if (Volatile.Read(ref _disposed) == 0)
            {
                Exited?.Invoke(this, (int)exitCode);
            }
        }
    }

    private static SafeFileHandle StartProcess(string commandLine, string workingDirectory, IntPtr pseudoConsole)
    {
        // Attach the process to the pseudo-console through an extended startup attribute.
        var attributeListSize = IntPtr.Zero;
        _ = NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);
        var attributeList = Marshal.AllocHGlobal(attributeListSize);
        try
        {
            Check(NativeMethods.InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize));
            Check(NativeMethods.UpdateProcThreadAttribute(
                attributeList, 0, NativeMethods.ProcThreadAttributePseudoConsole, pseudoConsole, IntPtr.Size, IntPtr.Zero, IntPtr.Zero));

            var startup = new NativeMethods.StartupInfoEx { AttributeList = attributeList };
            startup.StartupInfo.Size = Marshal.SizeOf<NativeMethods.StartupInfoEx>();

            // Explicitly no standard handles: otherwise a parent with redirected handles (a test runner,
            // some launchers) passes its own to the shell, which then bypasses the pseudo-console.
            startup.StartupInfo.Flags = NativeMethods.UseStdHandles;

            Check(NativeMethods.CreateProcess(
                null,
                (commandLine + "\0").ToCharArray(), // writable and null-terminated: CreateProcessW may modify it
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                NativeMethods.ExtendedStartupInfoPresent | NativeMethods.CreateUnicodeEnvironment,
                IntPtr.Zero,
                Directory.Exists(workingDirectory) ? workingDirectory : null,
                ref startup,
                out var process));

            NativeMethods.CloseHandle(process.Thread);
            return new SafeFileHandle(process.Process, ownsHandle: true);
        }
        finally
        {
            NativeMethods.DeleteProcThreadAttributeList(attributeList);
            Marshal.FreeHGlobal(attributeList);
        }
    }

    private static NativeMethods.Coord Size(int columns, int rows) =>
        new() { X = (short)Math.Clamp(columns, 1, short.MaxValue), Y = (short)Math.Clamp(rows, 1, short.MaxValue) };

    private static void Check(bool succeeded)
    {
        if (!succeeded)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    /// <summary>Win32 declarations for ConPTY and process creation.</summary>
    private static class NativeMethods
    {
        public const uint ExtendedStartupInfoPresent = 0x00080000;
        public const uint CreateUnicodeEnvironment = 0x00000400;
        public const uint Infinite = 0xFFFFFFFF;
        public const int UseStdHandles = 0x00000100;
        public static readonly IntPtr ProcThreadAttributePseudoConsole = 0x00020016;

        [StructLayout(LayoutKind.Sequential)]
        public struct Coord
        {
            public short X;
            public short Y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct StartupInfo
        {
            public int Size;
            public string? Reserved;
            public string? Desktop;
            public string? Title;
            public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
            public short ShowWindow, Reserved2Size;
            public IntPtr Reserved2, StdInput, StdOutput, StdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct StartupInfoEx
        {
            public StartupInfo StartupInfo;
            public IntPtr AttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ProcessInformation
        {
            public IntPtr Process;
            public IntPtr Thread;
            public int ProcessId;
            public int ThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, IntPtr attributes, int size);

        [DllImport("kernel32.dll")]
        public static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr pseudoConsole);

        [DllImport("kernel32.dll")]
        public static extern int ResizePseudoConsole(IntPtr pseudoConsole, Coord size);

        [DllImport("kernel32.dll")]
        public static extern void ClosePseudoConsole(IntPtr pseudoConsole);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previousValue, IntPtr returnSize);

        [DllImport("kernel32.dll")]
        public static extern void DeleteProcThreadAttributeList(IntPtr list);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessW")]
        public static extern bool CreateProcess(
            string? applicationName,
            char[] commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string? currentDirectory,
            ref StartupInfoEx startupInfo,
            out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetExitCodeProcess(SafeFileHandle process, out uint exitCode);
    }
}
