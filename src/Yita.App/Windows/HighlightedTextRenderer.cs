using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Yita.Translation;
using WpfFontFamily = System.Windows.Media.FontFamily;

namespace Yita.Windows;

/// <summary>
/// Creates selectable WPF runs from clean text plus emphasis metadata. The
/// foreground/background bindings stay dynamic so palette changes immediately
/// update already-open popup windows.
/// </summary>
internal static class HighlightedTextRenderer
{
    internal static void AppendTranslationRuns(
        Paragraph paragraph,
        HighlightedText text,
        WpfFontFamily englishFont,
        WpfFontFamily chineseFont)
    {
        foreach (var highlightedSegment in text.Segments)
        {
            foreach (var typographySegment in TranslationTypography.Segment(highlightedSegment.Text))
            {
                AppendRun(
                    paragraph,
                    typographySegment.Text,
                    typographySegment.UsesChineseFont ? chineseFont : englishFont,
                    highlightedSegment.Kind);
            }
        }
    }

    internal static void AppendUniformRuns(
        Paragraph paragraph,
        HighlightedText text,
        WpfFontFamily fontFamily)
    {
        foreach (var segment in text.Segments)
        {
            AppendRun(paragraph, segment.Text, fontFamily, segment.Kind);
        }
    }

    internal static bool HasHighlightRuns(Paragraph? paragraph)
    {
        return paragraph?.Inlines
            .OfType<Run>()
            .Any(run => run.Tag is HighlightKind kind && kind != HighlightKind.None) == true;
    }

    private static void AppendRun(
        Paragraph paragraph,
        string text,
        WpfFontFamily fontFamily,
        HighlightKind kind)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var run = new Run(text)
        {
            FontFamily = fontFamily,
            Tag = kind,
        };
        if (kind != HighlightKind.None)
        {
            run.FontWeight = FontWeights.SemiBold;
            run.SetResourceReference(
                TextElement.ForegroundProperty,
                GetForegroundResourceKey(kind));
            run.SetResourceReference(
                TextElement.BackgroundProperty,
                GetBackgroundResourceKey(kind));
        }

        paragraph.Inlines.Add(run);
    }

    private static string GetForegroundResourceKey(HighlightKind kind)
    {
        return kind switch
        {
            HighlightKind.Primary => "ContentHighlightPrimaryBrush",
            HighlightKind.Secondary => "ContentHighlightSecondaryBrush",
            HighlightKind.Tertiary => "ContentHighlightTertiaryBrush",
            _ => "PopupTextBrush",
        };
    }

    private static string GetBackgroundResourceKey(HighlightKind kind)
    {
        return kind switch
        {
            HighlightKind.Primary => "ContentHighlightPrimaryBackgroundBrush",
            HighlightKind.Secondary => "ContentHighlightSecondaryBackgroundBrush",
            HighlightKind.Tertiary => "ContentHighlightTertiaryBackgroundBrush",
            _ => "PopupBackgroundBrush",
        };
    }
}
