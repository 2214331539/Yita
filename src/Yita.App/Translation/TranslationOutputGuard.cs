namespace Yita.Translation;

/// <summary>Reject clear task-instruction echoes, not arbitrary language or code.</summary>
internal static class TranslationOutputGuard
{
    internal static bool IsInstructionEcho(string source, string output)
    {
        // Instructions about translation can themselves be legitimate source text.
        // This is a conservative quality check, not a general semantic validator.
        if (source.Contains("translat", StringComparison.OrdinalIgnoreCase)
            || source.Contains("翻译", StringComparison.Ordinal)
            || source.Contains("译文", StringComparison.Ordinal))
        {
            return false;
        }

        var plain = HighlightMarkup.ToPlainText(output);
        return plain.Contains("Your translation engine decides the language of your output", StringComparison.OrdinalIgnoreCase)
            || (plain.Contains("You are a", StringComparison.OrdinalIgnoreCase)
                && plain.Contains("translation engine", StringComparison.OrdinalIgnoreCase)
                && (plain.Contains("only output", StringComparison.OrdinalIgnoreCase)
                    || plain.Contains("Return only", StringComparison.OrdinalIgnoreCase)))
            || (plain.Contains("JSON field", StringComparison.OrdinalIgnoreCase)
                && plain.Contains("Translate only the value", StringComparison.OrdinalIgnoreCase));
    }
}
