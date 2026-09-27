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
                    HighlightKind.None);
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
            AppendRun(paragraph, segment.Text, fontFamily, HighlightKind.None);
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

        // Provider emphasis markup is retained for safe plain-text copying, but
        // Yita deliberately renders one calm reading color. Colored fragments
        // made long translations look like annotations and reduced contrast.
        var run = new Run(text)
        {
            FontFamily = fontFamily,
            Tag = HighlightKind.None,
        };
        run.SetResourceReference(TextElement.ForegroundProperty, "PopupTextBrush");

        paragraph.Inlines.Add(run);
    }

}
