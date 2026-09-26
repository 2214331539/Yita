using System.Text;
using Yita.Settings;

namespace Yita.Translation;

internal static class LanguageDirectionResolver
{
    internal const string Chinese = "简体中文";
    internal const string English = "英语";

    public static string ResolveTargetLanguage(string sourceText, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.TargetLanguageMode.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return settings.TargetLanguage;
        }

        var hasChinese = false;
        var hasEnglish = false;
        foreach (var rune in sourceText.EnumerateRunes())
        {
            hasChinese |= IsCjkIdeograph(rune.Value);
            hasEnglish |= rune.Value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
            if (hasChinese && hasEnglish)
            {
                return Chinese;
            }
        }

        return hasChinese ? English : Chinese;
    }

    public static string GetOppositeTarget(string currentTarget)
    {
        return currentTarget.Equals(English, StringComparison.OrdinalIgnoreCase)
            ? Chinese
            : English;
    }

    private static bool IsCjkIdeograph(int value)
    {
        return value is >= 0x3400 and <= 0x4DBF
            or >= 0x4E00 and <= 0x9FFF
            or >= 0xF900 and <= 0xFAFF
            or >= 0x20000 and <= 0x2EBEF;
    }
}
