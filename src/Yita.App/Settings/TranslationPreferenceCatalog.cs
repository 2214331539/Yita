namespace Yita.Settings;

internal static class TranslationPreferenceCatalog
{
    internal const string FastModeId = "fast";
    internal const string BalancedModeId = "balanced";
    internal const string PreciseModeId = "precise";
    internal const string DefaultModeId = BalancedModeId;

    internal const string NaturalToneId = "natural";
    internal const string FormalToneId = "formal";
    internal const string ConciseToneId = "concise";
    internal const string AcademicToneId = "academic";
    internal const string TechnicalToneId = "technical";
    internal const string DefaultToneId = NaturalToneId;

    internal static IReadOnlyList<string> ModeIds { get; } =
    [
        FastModeId,
        BalancedModeId,
        PreciseModeId,
    ];

    internal static IReadOnlyList<string> ToneIds { get; } =
    [
        NaturalToneId,
        FormalToneId,
        ConciseToneId,
        AcademicToneId,
        TechnicalToneId,
    ];

    internal static string NormalizeMode(string? value)
    {
        return ModeIds.FirstOrDefault(
                   candidate => candidate.Equals(value?.Trim(), StringComparison.OrdinalIgnoreCase))
               ?? DefaultModeId;
    }

    internal static string NormalizeTone(string? value)
    {
        return ToneIds.FirstOrDefault(
                   candidate => candidate.Equals(value?.Trim(), StringComparison.OrdinalIgnoreCase))
               ?? DefaultToneId;
    }
}
