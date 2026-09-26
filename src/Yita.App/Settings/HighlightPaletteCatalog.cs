namespace Yita.Settings;

internal sealed record HighlightPalette(
    string Id,
    string PrimaryForeground,
    string PrimaryBackground,
    string SecondaryForeground,
    string SecondaryBackground,
    string TertiaryForeground,
    string TertiaryBackground);

/// <summary>
/// Curated emphasis palettes. They are intentionally separate from the accent
/// color so window controls remain stable while reading colors can be changed.
/// </summary>
internal static class HighlightPaletteCatalog
{
    internal const string DefaultPaletteId = "yita";
    internal const string MorandiPaletteId = "morandi";
    internal const string OceanPaletteId = "ocean";
    internal const string WarmPaletteId = "warm";
    internal const string ContrastPaletteId = "contrast";

    internal static IReadOnlyList<HighlightPalette> Options { get; } =
    [
        new(
            DefaultPaletteId,
            "#21675E", "#E7F0E9",
            "#805233", "#F4E9DB",
            "#605D35", "#EFEDDC"),
        new(
            "clarity",
            "#1D4ED8", "#E8F0FF",
            "#7C3AED", "#F1EAFE",
            "#A16207", "#FFF3D8"),
        new(
            MorandiPaletteId,
            "#8A5A63", "#F3E5E8",
            "#58715D", "#E7EFE7",
            "#5B7181", "#E6EDF1"),
        new(
            OceanPaletteId,
            "#0F766E", "#DDF3F0",
            "#2563EB", "#E7EEFF",
            "#6D4BA3", "#F0E9FA"),
        new(
            WarmPaletteId,
            "#B45309", "#FFF0D9",
            "#BE3855", "#FBE7EC",
            "#6D5D9E", "#EEEAF8"),
        new(
            ContrastPaletteId,
            "#003C9E", "#CFE0FF",
            "#5C167D", "#F0D7FF",
            "#805100", "#FFE8A8"),
    ];

    internal static string Normalize(string? paletteId)
    {
        var candidate = paletteId?.Trim() ?? string.Empty;
        return Options.Any(option => option.Id.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            ? Options.First(option => option.Id.Equals(candidate, StringComparison.OrdinalIgnoreCase)).Id
            : DefaultPaletteId;
    }

    internal static HighlightPalette Resolve(string? paletteId)
    {
        var normalized = Normalize(paletteId);
        return Options.First(option => option.Id.Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }
}
