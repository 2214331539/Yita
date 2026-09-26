using System.IO;
using Microsoft.Win32;

namespace Yita.Settings;

internal sealed class StartupRegistration
{
    internal const string ValueName = "Yita";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public void SetEnabled(bool isEnabled)
    {
        if (!isEnabled)
        {
            using var existingKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            existingKey?.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            throw new InvalidOperationException("无法确定程序文件位置。");
        }

        using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开当前用户的开机启动设置。");
        runKey.SetValue(ValueName, BuildCommand(executablePath), RegistryValueKind.String);
    }

    internal static string BuildCommand(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (executablePath.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException("程序路径不能包含双引号。", nameof(executablePath));
        }

        return $"\"{executablePath}\"";
    }
}
