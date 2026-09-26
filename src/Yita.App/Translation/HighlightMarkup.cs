namespace Yita.Translation;

/// <summary>
/// Semantic emphasis levels emitted by the AI as presentation-only metadata.
/// The visible/copyable text is always exposed without the transport markers.
/// </summary>
internal enum HighlightKind
{
    None,
    Primary,
    Secondary,
    Tertiary,
}

internal readonly record struct HighlightedTextSegment(string Text, HighlightKind Kind);

/// <summary>
/// A clean text value together with safe, bounded inline emphasis ranges.
/// </summary>
internal sealed class HighlightedText
{
    private static readonly HighlightedText EmptyValue = new(string.Empty, []);
    private readonly HighlightedTextSegment[] _segments;

    private HighlightedText(string plainText, HighlightedTextSegment[] segments)
    {
        PlainText = plainText;
        _segments = segments;
    }

    internal static HighlightedText Empty => EmptyValue;

    internal string PlainText { get; }

    internal IReadOnlyList<HighlightedTextSegment> Segments => _segments;

    internal bool HasHighlights => _segments.Any(segment => segment.Kind != HighlightKind.None);

    internal static HighlightedText FromPlainText(string? text)
    {
        var normalized = text ?? string.Empty;
        return normalized.Length == 0
            ? Empty
            : new HighlightedText(normalized, [new HighlightedTextSegment(normalized, HighlightKind.None)]);
    }

    internal HighlightedText Prefix(int characterCount)
    {
        var requestedLength = Math.Clamp(characterCount, 0, PlainText.Length);
        if (requestedLength == 0)
        {
            return Empty;
        }

        if (requestedLength == PlainText.Length)
        {
            return this;
        }

        var remaining = requestedLength;
        var segments = new List<HighlightedTextSegment>();
        foreach (var segment in _segments)
        {
            if (remaining <= 0)
            {
                break;
            }

            var length = Math.Min(remaining, segment.Text.Length);
            AddSegment(segments, segment.Text[..length], segment.Kind);
            remaining -= length;
        }

        return Create(PlainText[..requestedLength], segments);
    }

    internal bool HasSamePresentation(HighlightedText? other)
    {
        if (other is null
            || !string.Equals(PlainText, other.PlainText, StringComparison.Ordinal)
            || _segments.Length != other._segments.Length)
        {
            return false;
        }

        for (var index = 0; index < _segments.Length; index++)
        {
            if (_segments[index] != other._segments[index])
            {
                return false;
            }
        }

        return true;
    }

    internal static HighlightedText Create(
        string plainText,
        IEnumerable<HighlightedTextSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        var normalizedSegments = new List<HighlightedTextSegment>();
        foreach (var segment in segments)
        {
            AddSegment(normalizedSegments, segment.Text, segment.Kind);
        }

        var calculatedText = string.Concat(normalizedSegments.Select(segment => segment.Text));
        if (!string.Equals(calculatedText, plainText, StringComparison.Ordinal))
        {
            return FromPlainText(plainText);
        }

        return plainText.Length == 0
            ? Empty
            : new HighlightedText(plainText, [.. normalizedSegments]);
    }

    internal static void AddSegment(
        ICollection<HighlightedTextSegment> segments,
        string? text,
        HighlightKind kind)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (segments is List<HighlightedTextSegment> list
            && list.Count > 0
            && list[^1].Kind == kind)
        {
            var previous = list[^1];
            list[^1] = previous with { Text = string.Concat(previous.Text, text) };
            return;
        }

        segments.Add(new HighlightedTextSegment(text, kind));
    }
}

/// <summary>
/// Parses the deliberately small inline protocol used by the AI to identify
/// important phrases. A malformed marker is never shown to the user or copied
/// into history: its available content falls back to normal text instead.
/// </summary>
internal static class HighlightMarkup
{
    private const int MarkerPrefixLength = 5;
    private const int MaximumHighlightedPhraseLength = 220;
    private const int MaximumFallbackHighlights = 3;
    private static readonly string[] StructuredLabels =
    [
        "释义：",
        "要点：",
        "语境：",
        "语言判断：",
        "常见用途：",
        "代码作用：",
        "关键逻辑：",
        "缩写与命名：",
        "替代与注意：",
        "Meaning:",
        "Key points:",
        "Context:",
        "Language:",
        "Common use:",
        "Code purpose:",
        "Key logic:",
        "Alternatives:",
    ];

    internal const string PromptInstruction = """
        Presentation emphasis is required for every non-empty response: mark one to three genuinely important short phrases with [[h1:phrase]], [[h2:phrase]], or [[h3:phrase]].
        Use at least one marker, and use two or three when the response contains multiple distinct key ideas.
        Keep the exact wording inside each marker, do not nest markers, do not span a line break, and do not mark an entire sentence or paragraph.
        These markers are display-only metadata: do not explain them, do not use Markdown, and never copy marker-like text from the input unless you intentionally add it as emphasis.
        """;

    internal static HighlightedText Parse(string? rawText)
    {
        if (string.IsNullOrEmpty(rawText))
        {
            return HighlightedText.Empty;
        }

        var segments = new List<HighlightedTextSegment>();
        var index = 0;
        while (index < rawText.Length)
        {
            var markerStart = rawText.IndexOf("[[h", index, StringComparison.Ordinal);
            if (markerStart < 0)
            {
                HighlightedText.AddSegment(segments, rawText[index..], HighlightKind.None);
                break;
            }

            if (markerStart > index)
            {
                HighlightedText.AddSegment(
                    segments,
                    rawText[index..markerStart],
                    HighlightKind.None);
            }

            if (!TryReadMarker(rawText, markerStart, out var kind, out var contentStart))
            {
                HighlightedText.AddSegment(segments, rawText[markerStart..(markerStart + 1)], HighlightKind.None);
                index = markerStart + 1;
                continue;
            }

            var markerEnd = rawText.IndexOf("]]", contentStart, StringComparison.Ordinal);
            if (markerEnd < 0)
            {
                // While a stream is still in flight, reveal the phrase without
                // leaking a partial marker. It is restyled when the close token arrives.
                HighlightedText.AddSegment(segments, rawText[contentStart..], HighlightKind.None);
                break;
            }

            var content = rawText[contentStart..markerEnd];
            var safeKind = IsSafeHighlightContent(content)
                ? kind
                : HighlightKind.None;
            HighlightedText.AddSegment(segments, content, safeKind);
            index = markerEnd + 2;
        }

        var plainText = string.Concat(segments.Select(segment => segment.Text));
        return HighlightedText.Create(plainText, segments);
    }

    internal static string ToPlainText(string? rawText) => Parse(rawText).PlainText;

    /// <summary>
    /// Guarantees a restrained visual cue when a provider returns valid plain
    /// text but omits the presentation protocol. This never changes the clean
    /// text used for copying, history, records, or later model context.
    /// </summary>
    internal static HighlightedText EnsureEmphasis(HighlightedText text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.HasHighlights || string.IsNullOrWhiteSpace(text.PlainText))
        {
            return text;
        }

        var ranges = FindStructuredLabelRanges(text.PlainText);
        if (ranges.Count == 0)
        {
            ranges = FindFallbackPhraseRanges(text.PlainText);
        }

        return ranges.Count == 0
            ? text
            : CreateEmphasizedText(text.PlainText, ranges);
    }

    private static bool TryReadMarker(
        string text,
        int markerStart,
        out HighlightKind kind,
        out int contentStart)
    {
        kind = HighlightKind.None;
        contentStart = markerStart;
        if (markerStart < 0
            || markerStart + MarkerPrefixLength > text.Length
            || text[markerStart] != '['
            || text[markerStart + 1] != '['
            || text[markerStart + 2] != 'h'
            || text[markerStart + 4] != ':')
        {
            return false;
        }

        kind = text[markerStart + 3] switch
        {
            '1' => HighlightKind.Primary,
            '2' => HighlightKind.Secondary,
            '3' => HighlightKind.Tertiary,
            _ => HighlightKind.None,
        };
        if (kind == HighlightKind.None)
        {
            return false;
        }

        contentStart = markerStart + MarkerPrefixLength;
        return true;
    }

    private static bool IsSafeHighlightContent(string content)
    {
        return !string.IsNullOrWhiteSpace(content)
            && content.Length <= MaximumHighlightedPhraseLength
            && content.IndexOf("[[h", StringComparison.Ordinal) < 0
            && content.IndexOf('\r') < 0
            && content.IndexOf('\n') < 0;
    }

    private static List<HighlightRange> FindStructuredLabelRanges(string text)
    {
        var ranges = new List<HighlightRange>();
        foreach (var label in StructuredLabels)
        {
            var startIndex = 0;
            while (startIndex < text.Length)
            {
                var index = text.IndexOf(label, startIndex, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    break;
                }

                ranges.Add(new HighlightRange(index, label.Length));
                startIndex = index + label.Length;
            }
        }

        return NormalizeRanges(ranges);
    }

    private static List<HighlightRange> FindFallbackPhraseRanges(string text)
    {
        var candidates = new List<HighlightRange>();
        for (var index = 0; index < text.Length;)
        {
            if (IsCjkCharacter(text[index]))
            {
                var start = index;
                while (index < text.Length && IsCjkCharacter(text[index]))
                {
                    index++;
                }

                var runLength = index - start;
                candidates.Add(new HighlightRange(start, Math.Min(runLength, 8)));
                if (runLength > 24)
                {
                    candidates.Add(new HighlightRange(start + (runLength / 2), Math.Min(8, runLength - (runLength / 2))));
                }

                continue;
            }

            if (char.IsLetterOrDigit(text[index]))
            {
                var start = index;
                while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] is '\'' or '’' or '-'))
                {
                    index++;
                }

                candidates.Add(new HighlightRange(start, Math.Min(index - start, 24)));
                continue;
            }

            index++;
        }

        var selected = new List<HighlightRange>();
        foreach (var desiredPosition in new[] { 0, text.Length / 3, (text.Length * 2) / 3 })
        {
            foreach (var candidate in candidates)
            {
                if (candidate.Start < desiredPosition
                    || selected.Any(selectedRange => RangesOverlap(selectedRange, candidate)))
                {
                    continue;
                }

                selected.Add(candidate);
                break;
            }
        }

        if (selected.Count == 0)
        {
            var fallbackStart = 0;
            while (fallbackStart < text.Length && char.IsWhiteSpace(text[fallbackStart]))
            {
                fallbackStart++;
            }

            if (fallbackStart < text.Length)
            {
                selected.Add(new HighlightRange(fallbackStart, 1));
            }
        }

        return NormalizeRanges(selected);
    }

    private static List<HighlightRange> NormalizeRanges(IEnumerable<HighlightRange> source)
    {
        var ranges = source
            .Where(range => range.Length > 0)
            .OrderBy(range => range.Start)
            .ThenBy(range => range.Length)
            .ToList();
        var normalized = new List<HighlightRange>();
        foreach (var range in ranges)
        {
            if (normalized.Count >= MaximumFallbackHighlights
                || normalized.Any(existing => RangesOverlap(existing, range)))
            {
                continue;
            }

            normalized.Add(range);
        }

        return normalized;
    }

    private static HighlightedText CreateEmphasizedText(
        string plainText,
        IReadOnlyList<HighlightRange> ranges)
    {
        var segments = new List<HighlightedTextSegment>();
        var cursor = 0;
        for (var index = 0; index < ranges.Count; index++)
        {
            var range = ranges[index];
            if (range.Start < cursor || range.Start >= plainText.Length)
            {
                continue;
            }

            var safeLength = Math.Min(range.Length, plainText.Length - range.Start);
            HighlightedText.AddSegment(segments, plainText[cursor..range.Start], HighlightKind.None);
            HighlightedText.AddSegment(
                segments,
                plainText.Substring(range.Start, safeLength),
                (HighlightKind)(index + 1));
            cursor = range.Start + safeLength;
        }

        HighlightedText.AddSegment(segments, plainText[cursor..], HighlightKind.None);
        return HighlightedText.Create(plainText, segments);
    }

    private static bool IsCjkCharacter(char character) => character is >= '\u4E00' and <= '\u9FFF';

    private static bool RangesOverlap(HighlightRange first, HighlightRange second) =>
        first.Start < second.End && second.Start < first.End;

    private readonly record struct HighlightRange(int Start, int Length)
    {
        internal int End => Start + Length;
    }
}
