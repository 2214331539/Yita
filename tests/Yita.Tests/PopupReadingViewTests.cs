using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Yita.Windows;

namespace Yita.Tests;

public sealed class PopupReadingViewTests
{
    [Fact]
    public void SourceView_PreservesStreamingDocumentAndSelectionAcrossSwitches()
    {
        RunSta(() =>
        {
            var popup = new PopupWindow(1, uiLanguage: "zh-CN");
            try
            {
                popup.UpdateSourceText("A quiet place to read. 一个安静的阅读空间。");
                var translated = (RichTextBox)popup.FindName("TranslationRichTextBox");
                var source = (RichTextBox)popup.FindName("SourceRichTextBox");
                var view = (Grid)popup.FindName("TranslationView");
                translated.Document = new FlowDocument(new Paragraph(new Run("安静阅读")));
                translated.SelectAll();
                var selected = translated.Selection.Text;
                var document = translated.Document;

                popup.SelectReadingView(true, animate: false);
                var sourceDocument = source.Document;
                Assert.Equal(Visibility.Visible, source.Visibility);
                Assert.Equal(Visibility.Collapsed, view.Visibility);
                Assert.Equal(selected, translated.Selection.Text);
                Assert.Contains("A quiet place", new TextRange(source.Document.ContentStart, source.Document.ContentEnd).Text);

                // More streamed content can arrive without replacing the visible source.
                ((Paragraph)document.Blocks.FirstBlock).Inlines.Add(new Run("，让理解更轻松。"));
                popup.UpdateSourceText("A quiet place to read. 一个安静的阅读空间。");
                Assert.Same(sourceDocument, source.Document);
                popup.SelectReadingView(false, animate: false);
                Assert.Same(document, translated.Document);
                Assert.Equal(Visibility.Visible, view.Visibility);
                Assert.Equal(Visibility.Collapsed, source.Visibility);
                Assert.Contains("让理解更轻松", new TextRange(document.ContentStart, document.ContentEnd).Text);

                foreach (var name in new[] { "SizePresetBar", "DirectionButton", "ExplainButton", "CodeAnalysisButton", "EditTranslationButton" })
                    Assert.Equal(Visibility.Collapsed, ((UIElement)popup.FindName(name)).Visibility);
                popup.SetFontSizeForVisualTest(22);
                Assert.Equal(22, source.Document.FontSize);
                Assert.Equal(22, translated.Document.FontSize);
            }
            finally { popup.Close(); }
        });
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "WPF reading view check timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
