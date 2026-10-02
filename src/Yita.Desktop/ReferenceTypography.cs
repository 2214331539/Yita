using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Yita.Settings;
using Yita.Windows;

namespace Yita.Desktop;

internal static class ReferenceTypography
{
    internal static void SetText(SelectableTextBlock block, string text, AppSettings settings)
    {
        block.Inlines ??= new InlineCollection();
        block.Text = null;
        block.Inlines.Clear();
        foreach (var segment in TranslationTypography.Segment(text))
            block.Inlines.Add(new Run(segment.Text)
            {
                FontFamily = CreateFont(segment.UsesChineseFont
                    ? settings.ChineseTranslationFontFamily : settings.EnglishTranslationFontFamily),
            });
    }
    internal static string GetText(TextBlock block) => block.Inlines?.Text ?? block.Text ?? "";
    internal static FontFamily CreateFont(string name) => new(name == "Source Sans Pro"
        ? "avares://Yita.Desktop/Assets/Fonts#Source Sans Pro" : name);
}
