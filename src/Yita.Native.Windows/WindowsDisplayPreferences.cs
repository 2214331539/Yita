using System.Runtime.InteropServices;

namespace Yita.Native.Windows;

public static class WindowsDisplayPreferences
{
    public static bool AnimationsEnabled
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return true;
            return !HighContrast && (!SystemParametersInfo(0x1042, 0, out uint enabled, 0) || enabled != 0);
        }
    }
    public static bool HighContrast
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            var info = new HighContrastInfo { Size = (uint)Marshal.SizeOf<HighContrastInfo>() };
            return GetHighContrast(0x42, info.Size, ref info, 0) && (info.Flags & 1) != 0;
        }
    }
    public static string SystemColor(int index)
    {
        var value = GetSysColor(index);
        return $"#{value & 0xFF:X2}{(value >> 8) & 0xFF:X2}{(value >> 16) & 0xFF:X2}";
    }
    [StructLayout(LayoutKind.Sequential)] private struct HighContrastInfo { public uint Size, Flags; public IntPtr Scheme; }
    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, uint parameter, out uint value, uint flags);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")] private static extern bool GetHighContrast(uint action, uint parameter, ref HighContrastInfo value, uint flags);
    [DllImport("user32.dll")] private static extern uint GetSysColor(int index);
}
