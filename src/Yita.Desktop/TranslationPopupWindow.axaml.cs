using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Yita.Core;
using Yita.Core.Selection;
using Yita.Core.Placement;

namespace Yita.Desktop;

public sealed partial class TranslationPopupWindow : Window
{
    private bool _dragging;
    private PixelPoint _dragStartPoint;
    private PixelPoint _dragStartPosition;
    private PopupOffset? _preferredOffset;
    private string _sourceText = string.Empty;
    private string _translatedText = "正在翻译…";
    private bool _showOriginal;
    private bool _hasError;
    private bool _userSized;
    private bool _hasAnchor;

    public TranslationPopupWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ConstrainToScreen();
        Closed += (_, _) => TranslationRequests.Dispose();
    }

    public event EventHandler<PopupMovedEventArgs>? Moved;
    public event EventHandler? Dismissed;
    public SelectionAnchor CurrentAnchor { get; private set; }
    public bool IsPinned => PinButton.IsChecked == true;
    internal LatestRequestController TranslationRequests { get; } = new();

    public void BeginTranslation(string source)
    {
        _sourceText = source;
        _translatedText = "正在翻译…";
        _hasError = false;
        _showOriginal = false;
        _userSized = false;
        ReadingScroll.Offset = default;
        RefreshText();
    }

    public void SetText(string text)
    {
        _translatedText = text;
        _hasError = false;
        RefreshText();
    }

    public void SetError(string text)
    {
        _translatedText = text;
        _hasError = true;
        RefreshText();
    }

    public void PlaceNear(SelectionRequest request, SelectionResult result, PopupOffset? savedOffset = null)
    {
        var anchor = result.Bounds is { IsValid: true, Width: > 0, Height: > 0 } bounds
            ? bounds.LowerLeft : request.GestureBounds?.LowerLeft ?? request.Pointer;
        if (!anchor.IsFinite) return;
        CurrentAnchor = new SelectionAnchor(anchor.X, anchor.Y);
        _hasAnchor = true;
        _preferredOffset = savedOffset;
        var screen = Screens.ScreenFromPoint(new PixelPoint((int)anchor.X, (int)anchor.Y)) ?? Screens.Primary;
        if (screen is not null)
        {
            MaxWidth = Math.Min(720, screen.WorkingArea.Width / screen.Scaling - 16);
            MaxHeight = Math.Min(520, screen.WorkingArea.Height / screen.Scaling - 16);
            MinWidth = Math.Min(280, MaxWidth);
            MinHeight = Math.Min(120, MaxHeight);
            Width = Math.Clamp(Width, MinWidth, MaxWidth);
        }
        PlaceAtPreferredOffset();
    }

    private void RefreshText()
    {
        TranslationText.Text = _showOriginal ? _sourceText : _translatedText;
        TranslationText.Foreground = new SolidColorBrush(Color.Parse(
            !_showOriginal && _hasError ? "#B34335" : "#173F43"));
        OriginalButton.Background = new SolidColorBrush(Color.Parse(_showOriginal ? "#E2F0EC" : "#FFFFFC"));
        TranslatedButton.Background = new SolidColorBrush(Color.Parse(_showOriginal ? "#FFFFFC" : "#E2F0EC"));
        if (_userSized) return;
        // Keep a stable reading width; only grow height during a streamed answer.
        TranslationText.Measure(new Size(Math.Max(100, Width - 52), double.PositiveInfinity));
        Height = Math.Clamp(TranslationText.DesiredSize.Height + 82, MinHeight, MaxHeight);
        ConstrainToScreen();
    }

    private void PlaceAtPreferredOffset()
    {
        if (!_hasAnchor) return;
        var screen = Screens.ScreenFromPoint(new PixelPoint((int)CurrentAnchor.X, (int)CurrentAnchor.Y)) ?? Screens.Primary;
        if (screen is null) return;
        var area = screen.WorkingArea;
        var position = PopupPlacementResolver.ResolveForScale(CurrentAnchor, new PopupSize(Width, Height),
            new WorkArea(area.X, area.Y, area.Width, area.Height), screen.Scaling, _preferredOffset);
        Position = new PixelPoint((int)Math.Round(position.X), (int)Math.Round(position.Y));
    }

    private void ConstrainToScreen()
    {
        if (!IsVisible || _dragging) return;
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;
        var area = screen.WorkingArea;
        var size = new PixelSize((int)Math.Ceiling(Width * screen.Scaling), (int)Math.Ceiling(Height * screen.Scaling));
        Position = new PixelPoint(
            Math.Clamp(Position.X, area.X, Math.Max(area.X, area.Right - size.Width)),
            Math.Clamp(Position.Y, area.Y, Math.Max(area.Y, area.Bottom - size.Height)));
    }

    private void HeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source && (source is Button || source is ToggleButton
            || source.GetVisualAncestors().Any(visual => visual is Button || visual is ToggleButton))) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        _dragStartPoint = this.PointToScreen(e.GetPosition(this));
        _dragStartPosition = Position;
        e.Pointer.Capture(sender as IInputElement ?? this);
        e.Handled = true;
    }

    private void HeaderPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging) return;
        var point = this.PointToScreen(e.GetPosition(this));
        Position = new PixelPoint(_dragStartPosition.X + point.X - _dragStartPoint.X,
            _dragStartPosition.Y + point.Y - _dragStartPoint.Y);
        e.Handled = true;
    }

    private void HeaderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging) return;
        FinishDrag();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void HeaderPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => FinishDrag();

    private void FinishDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        ConstrainToScreen();
        var screen = Screens.ScreenFromWindow(this);
        var offset = PopupPlacementResolver.CaptureLogicalOffset(CurrentAnchor, Position.X, Position.Y,
            screen?.Scaling ?? RenderScaling);
        if (!offset.IsValid) return;
        _preferredOffset = offset;
        Moved?.Invoke(this, new PopupMovedEventArgs(Position, offset));
    }

    private void ResizePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _userSized = true;
        BeginResizeDrag(WindowEdge.SouthEast, e);
        e.Handled = true;
    }

    private void OriginalClick(object? sender, RoutedEventArgs e) { _showOriginal = true; RefreshText(); }
    private void TranslatedClick(object? sender, RoutedEventArgs e) { _showOriginal = false; RefreshText(); }
    private void PinClick(object? sender, RoutedEventArgs e) => Topmost = true;

    private void CloseClick(object? sender, RoutedEventArgs e)
    {
        PinButton.IsChecked = false;
        TranslationRequests.Cancel();
        Hide();
        Dismissed?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class PopupMovedEventArgs(PixelPoint position, PopupOffset offset) : EventArgs
{
    public PixelPoint Position { get; } = position;
    public PopupOffset Offset { get; } = offset;
}
