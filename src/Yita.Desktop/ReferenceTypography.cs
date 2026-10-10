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
        Append(block.Inlines, text, settings);
    }
    internal static void Append(InlineCollection inlines, string text, AppSettings settings, FontWeight? weight = null)
    {
        foreach (var segment in TranslationTypography.Segment(text))
            inlines.Add(new Run(segment.Text)
            {
                FontFamily = CreateFont(segment.UsesChineseFont
                    ? settings.ChineseTranslationFontFamily : settings.EnglishTranslationFontFamily),
                FontWeight = weight ?? FontWeight.Normal,
            });
    }
    internal static string GetText(TextBlock block) => block.Inlines?.Text ?? block.Text ?? "";
    internal static FontFamily CreateFont(string name) => DesktopFontResolver.Resolve(name);
}
