using System.Text;

namespace Yita.Selection;

internal static class TextNormalizer
{
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (character != '\0')
            {
                builder.Append(character);
            }
        }

        return builder
            .ToString()
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
    }
}
