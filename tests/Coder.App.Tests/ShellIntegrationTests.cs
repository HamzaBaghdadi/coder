using Coder.App.Services;
using Microsoft.Win32;

namespace Coder.App.Tests;

/// <summary>
/// The "coder" command and "Open with Coder", installed into a throwaway registry key and folder (never
/// the real PATH or File Explorer menu).
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")] // skipped elsewhere
public sealed class ShellIntegrationTests : IDisposable
{
    private readonly string _keyPath = $@"Software\CoderTests\{Guid.NewGuid():N}";
    private readonly string _folder = Directory.CreateTempSubdirectory("coder-shell-").FullName;

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            Registry.CurrentUser.DeleteSubKeyTree(_keyPath, throwOnMissingSubKey: false);
            using (var parent = Registry.CurrentUser.OpenSubKey(@"Software\CoderTests"))
            {
                if (parent is { SubKeyCount: 0 })
                {
                    parent.Dispose();
                    Registry.CurrentUser.DeleteSubKey(@"Software\CoderTests", throwOnMissingSubKey: false);
                }
            }
        }

        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void Installs_the_command_and_menu_and_removes_them_again()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var root = Registry.CurrentUser.CreateSubKey(_keyPath);
        using (var environment = root.CreateSubKey("Environment"))
        {
            environment.SetValue("Path", @"%USERPROFILE%\tools;C:\Other", RegistryValueKind.ExpandString);
        }

        var exe = Path.Combine(_folder, "current", "Coder.exe");
        var bin = Path.Combine(_folder, "bin");
        var shell = new ShellIntegration(root, exe, bin, broadcast: false);
        Assert.False(shell.IsInstalled);

        shell.Install();
        shell.Install(); // again (every update does): nothing doubles
        Assert.True(shell.IsInstalled);
        Assert.Contains($"start \"\" \"{exe}\" %*", File.ReadAllText(Path.Combine(bin, "coder.cmd")), StringComparison.Ordinal);
        Assert.Contains(exe.Replace('\\', '/'), File.ReadAllText(Path.Combine(bin, "coder")), StringComparison.Ordinal);
        Assert.Equal($@"%USERPROFILE%\tools;C:\Other;{bin}", PathValue(root)); // kept unexpanded, added once
        Assert.Equal(RegistryValueKind.ExpandString, root.OpenSubKey("Environment")!.GetValueKind("Path"));

        foreach (var (key, argument) in new[] { (@"*\shell", "\"%1\""), (@"Directory\shell", "\"%V\""), (@"Directory\Background\shell", "\"%V\"") })
        {
            using var verb = root.OpenSubKey($@"Software\Classes\{key}\Coder");
            Assert.NotNull(verb);
            Assert.Equal("Open with Coder", verb.GetValue(null));
            Assert.Equal($"\"{exe}\" {argument}", verb.OpenSubKey("command")!.GetValue(null));
        }

        shell.Uninstall();
        Assert.False(shell.IsInstalled);
        Assert.Equal(@"%USERPROFILE%\tools;C:\Other", PathValue(root));
        Assert.Null(root.OpenSubKey(@"Software\Classes\*\shell\Coder"));
        Assert.Null(root.OpenSubKey(@"Software\Classes\Directory\Background\shell\Coder"));
        Assert.False(File.Exists(Path.Combine(bin, "coder.cmd")));
    }

    private static string? PathValue(RegistryKey root) =>
        root.OpenSubKey("Environment")!.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
}
