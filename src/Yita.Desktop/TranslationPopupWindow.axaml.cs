using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Yita.Core.Selection;
using Yita.Core.Placement;

namespace Yita.Desktop;

public sealed partial class TranslationPopupWindow : Window
{
    private bool _dragging;
    private Point _dragStartPoint;
    private PixelPoint _dragStartPosition;

    public TranslationPopupWindow() => InitializeComponent();

    public event EventHandler<PopupMovedEventArgs>? Moved;
    public event EventHandler? Dismissed;

    public SelectionAnchor CurrentAnchor { get; private set; }

    public void SetText(string text) => TranslationText.Text = text;

    public void SetError(string text)
    {
        TranslationText.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#B34335"));
        TranslationText.Text = text;
    }

    public void PlaceNear(SelectionRequest request, SelectionResult result, PopupOffset? savedOffset = null)
    {
        var anchor = request.GestureBounds?.LowerLeft
            ?? result.Bounds?.LowerLeft
            ?? request.Pointer;
        if (!anchor.IsFinite) return;
        CurrentAnchor = new SelectionAnchor(anchor.X, anchor.Y);
        var offset = savedOffset is { IsValid: true } value ? value : new PopupOffset(12, 12);
        Position = new PixelPoint(
            (int)Math.Round(anchor.X + offset.X),
            (int)Math.Round(anchor.Y + offset.Y));
    }

    private void HeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Button) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        _dragStartPoint = e.GetPosition(this);
        _dragStartPosition = Position;
        e.Pointer.Capture(sender as IInputElement ?? this);
        e.Handled = true;
    }

    private void HeaderPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging) return;
        var point = e.GetPosition(this);
        var delta = point - _dragStartPoint;
        Position = new PixelPoint(
            _dragStartPosition.X + (int)Math.Round(delta.X),
            _dragStartPosition.Y + (int)Math.Round(delta.Y));
        e.Handled = true;
    }

    private void HeaderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        var offset = new PopupOffset(Position.X - CurrentAnchor.X, Position.Y - CurrentAnchor.Y);
        if (offset.IsValid) Moved?.Invoke(this, new PopupMovedEventArgs(Position, offset));
        e.Handled = true;
    }

    private void CloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Hide();
        Dismissed?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class PopupMovedEventArgs(PixelPoint position, PopupOffset offset) : EventArgs
{
    public PixelPoint Position { get; } = position;
    public PopupOffset Offset { get; } = offset;
}
