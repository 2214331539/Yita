using Avalonia.Media;

namespace Yita.Desktop;

internal enum DesktopFontPlatform { Windows, Mac, Other }

internal static class DesktopFontResolver
{
    internal const string BundledSans = "avares://Yita.Desktop/Assets/Fonts#Source Sans Pro";
    internal static DesktopFontPlatform Platform => OperatingSystem.IsWindows() ? DesktopFontPlatform.Windows
        : OperatingSystem.IsMacOS() ? DesktopFontPlatform.Mac : DesktopFontPlatform.Other;
    private static readonly Lazy<HashSet<string>> Installed = new(() =>
        FontManager.Current.SystemFonts.Select(font => font.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));

    internal static FontFamily InterfaceFont => new(ResolveInterface(Platform, IsInstalled));
    internal static FontFamily Resolve(string name) => new(ResolveName(name, Platform, IsInstalled));
    private static bool IsInstalled(string name) => Installed.Value.Contains(name);

    internal static string ResolveInterface(DesktopFontPlatform platform, Func<string, bool> installed) => platform switch
    {
        DesktopFontPlatform.Windows => "Microsoft YaHei",
        DesktopFontPlatform.Mac => First(["PingFang SC", "Heiti SC", "Helvetica Neue"], installed),
        _ => First(["Noto Sans CJK SC", "Noto Sans", "DejaVu Sans"], installed),
    };

    internal static string ResolveName(string name, DesktopFontPlatform platform, Func<string, bool> installed)
    {
        if (name == "Source Sans Pro") return BundledSans;
        // Keep the accepted Windows typography and saved preference IDs unchanged.
        if (platform == DesktopFontPlatform.Windows) return name;
        if (installed(name)) return name;
        string[] candidates = platform == DesktopFontPlatform.Mac ? name switch
        {
            "Microsoft YaHei" or "Microsoft YaHei UI" or "SimHei" => ["PingFang SC", "Heiti SC"],
            "SimSun" => ["Songti SC", "STSong", "PingFang SC"],
            "KaiTi" => ["Kaiti SC", "STKaiti", "PingFang SC"],
            "Times New Roman" or "Cambria" => ["Times", "Georgia", "Helvetica Neue"],
            "Georgia" => ["Georgia", "Times", "Helvetica Neue"],
            _ => ["Helvetica Neue", "Helvetica", "Arial", "PingFang SC"],
        } : ["Noto Sans CJK SC", "Noto Sans", "DejaVu Sans"];
        return First(candidates, installed);
    }

    private static string First(string[] candidates, Func<string, bool> installed) =>
        candidates.FirstOrDefault(installed) ?? FontFamily.Default.Name;
}
