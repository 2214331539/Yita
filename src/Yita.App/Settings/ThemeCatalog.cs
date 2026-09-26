using System.Globalization;

namespace Yita.Settings;

internal sealed record ThemeOption(string Id, string DisplayName, string AccentColor);

internal sealed record ThemePalette(
    string Accent,
    string AccentLight,
    string PopupBackground,
    string PopupBorder,
    string PopupButton,
    string PopupButtonHover,
    string PopupButtonBorder,
    string PopupText,
    string PopupMuted,
    string PopupBadge);

internal static class ThemeCatalog
{
    internal const string DefaultThemeId = "yita";
    internal const string CustomThemeId = "custom";
    internal const string DefaultCustomAccent = "#24756B";

    internal static IReadOnlyList<ThemeOption> Options { get; } =
    [
        new("yita", "译獭 · 湖畔", "#24756B"),
        new("ocean", "深海蓝", "#2563EB"),
        new("violet", "紫罗兰", "#7C3AED"),
        new("emerald", "翡翠绿", "#059669"),
        new("sunset", "暖橙色", "#EA580C"),
        new("rose", "玫瑰红", "#E11D48"),
        new(CustomThemeId, "自定义", DefaultCustomAccent),
    ];

    internal static ThemePalette Resolve(string? themeId, string? customAccentColor)
    {
        var option = Options.FirstOrDefault(item => item.Id.Equals(themeId, StringComparison.OrdinalIgnoreCase))
            ?? Options[0];
        var accent = option.Id == CustomThemeId && TryNormalizeHexColor(customAccentColor, out var normalized)
            ? normalized
            : option.AccentColor;

        if (option.Id == DefaultThemeId)
        {
            return new ThemePalette(accent, "#1C625A", "#FFFCF7", "#DDD5C9",
                "#EFE9DF", "#E3EDE7", "#C7BDAE", "#302D29", "#696158", accent);
        }

        return new ThemePalette(
            accent,
            Blend(accent, "#FFFFFF", 0.38),
            Blend("#F7F7F8", accent, 0.018),
            Blend("#E2E2E5", accent, 0.06),
            Blend("#EEEEF0", accent, 0.025),
            Blend("#DEDEE2", accent, 0.055),
            Blend("#D7D7DB", accent, 0.08),
            "#1D1D1F",
            "#6E6E73",
            accent);
    }

    internal static bool TryNormalizeHexColor(string? value, out string normalized)
    {
        var candidate = value?.Trim() ?? string.Empty;
        if (!candidate.StartsWith('#'))
        {
            candidate = "#" + candidate;
        }

        if (candidate.Length == 7
            && int.TryParse(candidate.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
        {
            normalized = candidate.ToUpperInvariant();
            return true;
        }

        normalized = DefaultCustomAccent;
        return false;
    }

    private static string Blend(string first, string second, double secondWeight)
    {
        var firstRgb = ParseRgb(first);
        var secondRgb = ParseRgb(second);
        var firstWeight = 1 - secondWeight;
        return $"#{BlendChannel(firstRgb.R, secondRgb.R, firstWeight, secondWeight):X2}"
             + $"{BlendChannel(firstRgb.G, secondRgb.G, firstWeight, secondWeight):X2}"
             + $"{BlendChannel(firstRgb.B, secondRgb.B, firstWeight, secondWeight):X2}";
    }

    private static (byte R, byte G, byte B) ParseRgb(string color)
    {
        return (
            byte.Parse(color.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(color.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(color.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    private static byte BlendChannel(byte first, byte second, double firstWeight, double secondWeight)
    {
        return (byte)Math.Clamp((int)Math.Round(first * firstWeight + second * secondWeight), 0, 255);
    }
}
