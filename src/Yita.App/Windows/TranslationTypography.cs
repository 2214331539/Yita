using System.Text;

namespace Yita.Windows;

internal sealed record TranslationTextSegment(string Text, bool UsesChineseFont);

internal static class TranslationTypography
{
    internal static IReadOnlyList<TranslationTextSegment> Segment(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var segments = new List<TranslationTextSegment>();
        var current = new StringBuilder();
        bool? currentUsesChineseFont = null;

        foreach (var rune in text.EnumerateRunes())
        {
            var usesChineseFont = Rune.IsWhiteSpace(rune)
                ? currentUsesChineseFont ?? false
                : IsChinese(rune.Value);
            if (currentUsesChineseFont is not null && currentUsesChineseFont != usesChineseFont)
            {
                segments.Add(new TranslationTextSegment(current.ToString(), currentUsesChineseFont.Value));
                current.Clear();
            }

            currentUsesChineseFont = usesChineseFont;
            current.Append(rune.ToString());
        }

        if (current.Length > 0)
        {
            segments.Add(new TranslationTextSegment(current.ToString(), currentUsesChineseFont ?? false));
        }

        return segments;
    }

    private static bool IsChinese(int value)
    {
        return value is >= 0x2E80 and <= 0x2FFF
            or >= 0x3000 and <= 0x303F
            or >= 0x31C0 and <= 0x31EF
            or >= 0x3400 and <= 0x4DBF
            or >= 0x4E00 and <= 0x9FFF
            or >= 0xF900 and <= 0xFAFF
            or >= 0xFE30 and <= 0xFE4F
            or >= 0xFF00 and <= 0xFFEF
            or >= 0x20000 and <= 0x2EBEF;
    }
}
