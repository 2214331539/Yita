namespace Yita.Settings;

internal enum SummaryRange
{
    Today,
    LastSevenDays,
    All,
}

internal static class SummaryRangeCatalog
{
    internal static SummaryRange Normalize(SummaryRange value) =>
        Enum.IsDefined(value) ? value : SummaryRange.Today;

    internal static string ToStableId(SummaryRange value) => Normalize(value) switch
    {
        SummaryRange.LastSevenDays => "last-7-days",
        SummaryRange.All => "all",
        _ => "today",
    };

    internal static SummaryRange FromStableId(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "last-7-days" => SummaryRange.LastSevenDays,
        "all" => SummaryRange.All,
        _ => SummaryRange.Today,
    };
}
