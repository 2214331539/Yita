using Microsoft.Win32;

namespace Yita.Native.Windows;

public sealed class WindowsStartupService : Yita.Core.Platform.IStartupRegistration
{
    public bool IsSupported => OperatingSystem.IsWindows();
    public void Apply(bool enabled) => WindowsStartupRegistration.Apply(enabled);
}

/// <summary>
/// Registers Yita for the current Windows user without requiring elevation.
/// </summary>
public static class WindowsStartupRegistration
{
    private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string ValueName = "Yita.CrossPlatform";

    public static void Apply(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows startup registration is unavailable.");

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开 Windows 开机启动注册表项。");
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
            throw new InvalidOperationException("无法确定 Yita 可执行文件路径。");

        var assemblyPath = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        var runtimeRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        var portableHost = string.IsNullOrEmpty(runtimeRoot) ? null : Path.Combine(runtimeRoot, "dotnet.exe");
        var command = portableHost is not null && File.Exists(portableHost) && !string.IsNullOrEmpty(assemblyPath)
            ? Quote(portableHost) + " " + Quote(assemblyPath) + " --background"
            : Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(assemblyPath)
                ? Quote(executable) + " " + Quote(assemblyPath) + " --background"
                : Quote(executable) + " --background";
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }

    private static string Quote(string path) =>
        path.Contains(' ', StringComparison.Ordinal) ? $"\"{path}\"" : path;
}
