using Microsoft.Win32;

namespace Yita.Native.Windows;

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

        key.SetValue(ValueName, Quote(executable), RegistryValueKind.String);
    }

    private static string Quote(string path) =>
        path.Contains(' ', StringComparison.Ordinal) ? $"\"{path}\"" : path;
}
