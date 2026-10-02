using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Utilities;

namespace Yita.Desktop;

// Keep one selectable transcript while matching WPF's compact paragraph gaps.
public sealed class TranscriptTextBlock : SelectableTextBlock
{
    protected override TextLayout CreateTextLayout(string? text)
    {
        if (_textRuns is null) return base.CreateTextLayout(text);
        var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
        var defaults = new GenericTextRunProperties(typeface, FontFeatures, 6, TextDecorations, Foreground);
        var paragraphs = new GenericTextParagraphProperties(FlowDirection, TextAlignment, true, false,
            defaults, TextWrapping, LineHeight, 0, LetterSpacing);
        var start = Math.Min(SelectionStart, SelectionEnd);
        var length = Math.Abs(SelectionEnd - SelectionStart);
        List<ValueSpan<TextRunProperties>>? selection = null;
        if (length > 0 && SelectionForegroundBrush is not null)
        {
            selection = [];
            var position = 0;
            foreach (var run in _textRuns)
            {
                var from = Math.Max(start, position);
                var to = Math.Min(start + length, position + run.Length);
                if (to > from && run.Properties is { } properties)
                    selection.Add(new ValueSpan<TextRunProperties>(from, to - from,
                        new GenericTextRunProperties(properties.Typeface, properties.FontFeatures,
                            properties.FontRenderingEmSize, properties.TextDecorations, SelectionForegroundBrush)));
                position += run.Length;
            }
        }
        // Runs carry their own font size. A small default avoids imposing a
        // full-size line box on the spacer runs between messages.
        return new TextLayout(new InlinesTextSource(_textRuns, selection), paragraphs, TextTrimming,
            double.IsNaN(_constraint.Width) ? 0 : _constraint.Width,
            double.IsNaN(_constraint.Height) ? 0 : _constraint.Height, MaxLines);
    }
}
