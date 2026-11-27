using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Coder.Presentation.Services;
using Microsoft.Win32;

namespace Coder.App.Services;

/// <summary>
/// Coder in Windows, like VS Code: the <c>coder</c> command in terminals (<c>coder</c>, <c>coder .</c>,
/// <c>coder file.ts</c>) and "Open with Coder" when right-clicking a file, a folder or a folder's
/// background in File Explorer. Everything is per user (HKEY_CURRENT_USER and the user's PATH), so it
/// needs no administrator rights. The installer sets it up and the uninstaller removes it; the command
/// palette can do either too.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ShellIntegration : IShellIntegration
{
    public const string MenuText = "Open with Coder";
    private const string Verb = "Coder";

    private readonly RegistryKey _root;
    private readonly string _exe;
    private readonly string _bin;
    private readonly bool _broadcast;

    /// <param name="root">Where the keys go: HKEY_CURRENT_USER, or a throwaway key in tests.</param>
    /// <param name="exe">Coder.exe; Velopack keeps it at the same path across updates (…\Coder\current\Coder.exe).</param>
    /// <param name="bin">The folder put on PATH, holding the <c>coder</c> scripts.</param>
    public ShellIntegration(RegistryKey root, string exe, string bin, bool broadcast = true)
    {
        _root = root;
        _exe = exe;
        _bin = bin;
        _broadcast = broadcast;
    }

    /// <summary>For the running Coder: an installed one keeps its scripts next to its "current" folder.</summary>
    public static ShellIntegration ForThisApp()
    {
        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Coder.exe");
        var folder = Path.GetDirectoryName(exe)!;
        var bin = string.Equals(Path.GetFileName(folder), "current", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Path.GetDirectoryName(folder)!, "bin")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Coder", "bin");
        return new ShellIntegration(Registry.CurrentUser, exe, bin);
    }

    public string BinFolder => _bin;

    public bool IsInstalled =>
        File.Exists(Path.Combine(_bin, "coder.cmd")) && PathEntries().Contains(_bin, StringComparer.OrdinalIgnoreCase);

    public string Install()
    {
        // coder.cmd for cmd and PowerShell; "coder" (no extension) for Git Bash. Both return at once.
        Directory.CreateDirectory(_bin);
        File.WriteAllText(Path.Combine(_bin, "coder.cmd"), $"@echo off\r\nstart \"\" \"{_exe}\" %*\r\n");
        File.WriteAllText(Path.Combine(_bin, "coder"), $"#!/usr/bin/env sh\n\"{_exe.Replace('\\', '/')}\" \"$@\" >/dev/null 2>&1 &\n");

        var entries = PathEntries();
        if (!entries.Contains(_bin, StringComparer.OrdinalIgnoreCase))
        {
            SetPath([.. entries, _bin]);
        }

        // Right-click a file, a folder, or the empty space in a folder.
        AddVerb(@"Software\Classes\*\shell", "\"%1\"");
        AddVerb(@"Software\Classes\Directory\shell", "\"%V\"");
        AddVerb(@"Software\Classes\Directory\Background\shell", "\"%V\"");
        AddVerb(@"Software\Classes\Drive\shell", "\"%V\"");
        Broadcast();
        return $"Installed: type \"coder .\" in a new terminal, or right-click in File Explorer and choose \"{MenuText}\".";
    }

    public string Uninstall()
    {
        foreach (var key in new[] { @"Software\Classes\*\shell", @"Software\Classes\Directory\shell", @"Software\Classes\Directory\Background\shell", @"Software\Classes\Drive\shell" })
        {
            _root.DeleteSubKeyTree($@"{key}\{Verb}", throwOnMissingSubKey: false);
        }

        var entries = PathEntries();
        if (entries.Contains(_bin, StringComparer.OrdinalIgnoreCase))
        {
            SetPath(entries.Where(e => !string.Equals(e, _bin, StringComparison.OrdinalIgnoreCase)).ToList());
        }

        foreach (var script in new[] { "coder.cmd", "coder" })
        {
            try
            {
                File.Delete(Path.Combine(_bin, script));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // left behind harmlessly; the folder is no longer on PATH
            }
        }

        Broadcast();
        return $"Removed the \"coder\" command and \"{MenuText}\".";
    }

    private void AddVerb(string shell, string argument)
    {
        using var verb = _root.CreateSubKey($@"{shell}\{Verb}");
        verb.SetValue(null, MenuText);
        verb.SetValue("Icon", $"\"{_exe}\",0");
        using var command = verb.CreateSubKey("command");
        command.SetValue(null, $"\"{_exe}\" {argument}");
    }

    /// <summary>The user's own PATH (not the machine's), unexpanded, as the user set it.</summary>
    private List<string> PathEntries()
    {
        using var environment = _root.OpenSubKey("Environment");
        var value = environment?.GetValue("Path", string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? string.Empty;
        return value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private void SetPath(IReadOnlyList<string> entries)
    {
        using var environment = _root.CreateSubKey("Environment");
        environment.SetValue("Path", string.Join(';', entries), RegistryValueKind.ExpandString);
    }

    /// <summary>Tells Explorer and new terminals that PATH changed, so no sign-out is needed.</summary>
    private void Broadcast()
    {
        if (_broadcast)
        {
            _ = SendMessageTimeout(new IntPtr(0xFFFF), 0x001A, IntPtr.Zero, "Environment", 0x0002, 2000, out _); // HWND_BROADCAST, WM_SETTINGCHANGE, SMTO_ABORTIFHUNG
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED: the right-click menu refreshes
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("shell32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
