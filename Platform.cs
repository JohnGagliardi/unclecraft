using System.Runtime.InteropServices;

namespace McShared;

/// <summary>What differs between the Windows and macOS builds of UncleCraft (shared code asks here).</summary>
public static class Platform
{
    public const string AppName = "UncleCraft";
    /// <summary>What the window title bar and the launcher's heading show. The program, shortcuts and folders keep AppName.</summary>
    public const string Title = "Family Server";
    public static bool IsMac => OperatingSystem.IsMacOS();
    public static bool IsWindows => OperatingSystem.IsWindows();
    public static bool IsArm64 => RuntimeInformation.OSArchitecture == Architecture.Arm64;
    public static string Name => IsMac ? "mac" : IsWindows ? "windows" : "linux";

    /// <summary>"os.name" in Mojang's version-JSON rules.</summary>
    public static string RulesOs => IsMac ? "osx" : IsWindows ? "windows" : "linux";

    /// <summary>Key in Mojang's Java runtime index (Windows stays x64; it runs under emulation on ARM PCs too).</summary>
    public static string JavaRuntimeKey => IsMac ? (IsArm64 ? "mac-os-arm64" : "mac-os") : IsWindows ? "windows-x64" : "linux";

    /// <summary>The java binary inside a Mojang runtime folder.</summary>
    public static string JavaExeRelative => IsMac ? "jre.bundle/Contents/Home/bin/java" : IsWindows ? "bin/javaw.exe" : "bin/java";

    public static string Describe() => $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}), .NET {Environment.Version}";
}
