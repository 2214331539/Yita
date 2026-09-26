namespace Yita.Settings;

internal static class PopupVisualStyleCatalog
{
    internal const string MinimalStyleId = "minimal";
    internal const string BubbleStyleId = "bubble";
    internal const string BubbleV2StyleId = "bubble-v2";
    internal const string BubbleV3StyleId = "bubble-v3";
    internal const string BubbleV3ColorStyleId = "bubble-v3-color";
    internal const string DefaultStyleId = MinimalStyleId;

    internal static string Normalize(string? styleId)
    {
        var normalized = styleId?.Trim();
        if (string.Equals(normalized, BubbleV3ColorStyleId, StringComparison.OrdinalIgnoreCase))
        {
            return BubbleV3ColorStyleId;
        }

        if (string.Equals(normalized, BubbleV3StyleId, StringComparison.OrdinalIgnoreCase))
        {
            return BubbleV3StyleId;
        }

        if (string.Equals(normalized, BubbleV2StyleId, StringComparison.OrdinalIgnoreCase))
        {
            return BubbleV2StyleId;
        }

        return string.Equals(normalized, BubbleStyleId, StringComparison.OrdinalIgnoreCase)
            ? BubbleStyleId
            : MinimalStyleId;
    }

    internal static bool IsBubble(string? styleId)
    {
        var normalized = Normalize(styleId);
        return string.Equals(normalized, BubbleStyleId, StringComparison.Ordinal)
               || string.Equals(normalized, BubbleV2StyleId, StringComparison.Ordinal)
               || string.Equals(normalized, BubbleV3StyleId, StringComparison.Ordinal)
               || string.Equals(normalized, BubbleV3ColorStyleId, StringComparison.Ordinal);
    }

    internal static bool IsBubbleV2(string? styleId) =>
        string.Equals(Normalize(styleId), BubbleV2StyleId, StringComparison.Ordinal);

    internal static bool IsBubbleV3(string? styleId) =>
        Normalize(styleId) is BubbleV3StyleId or BubbleV3ColorStyleId;

    internal static bool IsBubbleV3Color(string? styleId) =>
        string.Equals(Normalize(styleId), BubbleV3ColorStyleId, StringComparison.Ordinal);
}
