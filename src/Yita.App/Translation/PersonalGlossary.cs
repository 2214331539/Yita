namespace Yita.Translation;

internal sealed record GlossaryEntry(string Source, string Target);

internal static class PersonalGlossary
{
    internal const int MaximumEntries = 100;
    internal const int MaximumTermLength = 160;

    internal static IReadOnlyList<GlossaryEntry> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var entries = new List<GlossaryEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (entries.Count >= MaximumEntries)
            {
                break;
            }

            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = FindSeparator(line);
            if (separator.Index < 1)
            {
                continue;
            }

            var source = line[..separator.Index].Trim();
            var target = line[(separator.Index + separator.Length)..].Trim();
            if (source.Length == 0
                || target.Length == 0
                || source.Length > MaximumTermLength
                || target.Length > MaximumTermLength
                || !seen.Add(source))
            {
                continue;
            }

            entries.Add(new GlossaryEntry(source, target));
        }

        return entries;
    }

    private static (int Index, int Length) FindSeparator(string line)
    {
        foreach (var separator in new[] { "=>", "→", "\t", "=" })
        {
            var index = line.IndexOf(separator, StringComparison.Ordinal);
            if (index >= 0)
            {
                return (index, separator.Length);
            }
        }

        return (-1, 0);
    }
}
