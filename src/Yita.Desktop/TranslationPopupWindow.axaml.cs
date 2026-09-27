using Avalonia;
using Avalonia.Controls;
using Yita.Core.Selection;

namespace Yita.Desktop;

public sealed partial class TranslationPopupWindow : Window
{
    public TranslationPopupWindow() => InitializeComponent();

    public void SetText(string text) => TranslationText.Text = text;

    public void SetError(string text)
    {
        TranslationText.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#B34335"));
        TranslationText.Text = text;
    }

    public void PlaceNear(SelectionRequest request, SelectionResult result)
    {
        var anchor = result.Bounds?.LowerLeft ?? request.Pointer;
        if (!anchor.IsFinite) return;
        Position = new PixelPoint((int)Math.Round(anchor.X + 12), (int)Math.Round(anchor.Y + 12));
    }

    private void CloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
}
