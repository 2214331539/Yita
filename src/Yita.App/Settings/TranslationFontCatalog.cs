namespace Yita.Settings;

internal sealed record TranslationFontOption(string FamilyName, string DisplayName);

internal static class TranslationFontCatalog
{
    internal const string DefaultEnglishFontFamily = "Segoe UI";
    internal const string DefaultChineseFontFamily = "Microsoft YaHei UI";

    internal static IReadOnlyList<TranslationFontOption> EnglishOptions { get; } =
    [
        new(DefaultEnglishFontFamily, "Segoe UI"),
        new("Times New Roman", "Times New Roman"),
        new("Arial", "Arial"),
        new("Source Sans Pro", "Source Sans Pro"),
        new("Georgia", "Georgia"),
        new("Calibri", "Calibri"),
        new("Cambria", "Cambria"),
    ];

    internal static IReadOnlyList<TranslationFontOption> ChineseOptions { get; } =
    [
        new(DefaultChineseFontFamily, "微软雅黑 (Microsoft YaHei UI)"),
        new("SimHei", "黑体 (SimHei)"),
        new("SimSun", "宋体 (SimSun)"),
        new("KaiTi", "楷体 (KaiTi)"),
    ];

    internal static string NormalizeEnglish(string? value)
    {
        return Normalize(value, EnglishOptions, DefaultEnglishFontFamily, EnglishAliases);
    }

    internal static string NormalizeChinese(string? value)
    {
        return Normalize(value, ChineseOptions, DefaultChineseFontFamily, ChineseAliases);
    }

    internal static bool IsSupportedEnglish(string? value) => IsSupported(value, EnglishOptions);

    internal static bool IsSupportedChinese(string? value) => IsSupported(value, ChineseOptions);

    private static readonly IReadOnlyDictionary<string, string> EnglishAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["TimesNewRoman"] = "Times New Roman",
            ["SourceSansPro"] = "Source Sans Pro",
        };

    private static readonly IReadOnlyDictionary<string, string> ChineseAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["黑体"] = "SimHei",
            ["微软雅黑"] = "Microsoft YaHei UI",
            ["Microsoft YaHei"] = "Microsoft YaHei UI",
            ["宋体"] = "SimSun",
            ["楷体"] = "KaiTi",
        };

    private static string Normalize(
        string? value,
        IReadOnlyList<TranslationFontOption> options,
        string fallback,
        IReadOnlyDictionary<string, string> aliases)
    {
        var candidate = value?.Trim();
        if (string.IsNullOrEmpty(candidate))
        {
            return fallback;
        }

        var supported = options.FirstOrDefault(
            option => option.FamilyName.Equals(candidate, StringComparison.OrdinalIgnoreCase));
        if (supported is not null)
        {
            return supported.FamilyName;
        }

        return aliases.TryGetValue(candidate, out var canonicalName)
            ? canonicalName
            : fallback;
    }

    private static bool IsSupported(string? value, IReadOnlyList<TranslationFontOption> options)
    {
        return !string.IsNullOrWhiteSpace(value)
            && options.Any(
                option => option.FamilyName.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
