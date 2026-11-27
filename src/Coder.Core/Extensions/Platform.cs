using System.Runtime.InteropServices;

namespace Coder.Core.Extensions;

/// <summary>This machine as .NET runtime identifiers name it, which program extensions ship per platform for.</summary>
public static class Platform
{
    /// <summary>"win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64" or "osx-arm64".</summary>
    public static string RuntimeId { get; } = Compute(RuntimeInformation.ProcessArchitecture);

    internal static string Compute(Architecture architecture)
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var cpu = architecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "x64",
        };
        return $"{os}-{cpu}";
    }
}
