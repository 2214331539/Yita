namespace Yita.Core.Selection;

public enum SelectionTrigger
{
    MouseGesture,
    TranslateShortcut,
    TrayCommand,
}

public enum SelectionSource
{
    Accessibility,
    ClipboardFallback,
    ManualClipboard,
}

public enum SelectionFailureKind
{
    None,
    Empty,
    PermissionDenied,
    UnsupportedApplication,
    Timeout,
    ClipboardUnavailable,
    ProtectedContent,
    Cancelled,
    Unknown,
}

public readonly record struct ScreenPoint(double X, double Y)
{
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);
}

public readonly record struct SelectionBounds(double X, double Y, double Width, double Height)
{
    public bool IsValid => double.IsFinite(X) && double.IsFinite(Y)
        && double.IsFinite(Width) && double.IsFinite(Height)
        && Width >= 0 && Height >= 0;

    public ScreenPoint LowerLeft => new(X, Y + Height);
}

public sealed record SelectionRequest(
    SelectionTrigger Trigger,
    ScreenPoint Pointer,
    string? ForegroundApplication = null,
    SelectionBounds? GestureBounds = null);

public readonly record struct SelectionGesture(
    ScreenPoint Start,
    ScreenPoint End,
    DateTimeOffset CompletedAt)
{
    public bool IsFinite => Start.IsFinite && End.IsFinite;

    public SelectionBounds Bounds => new(
        Math.Min(Start.X, End.X),
        Math.Min(Start.Y, End.Y),
        Math.Abs(End.X - Start.X),
        Math.Abs(End.Y - Start.Y));

    public ScreenPoint PopupAnchor => Bounds.LowerLeft;
}

/// <summary>
/// Converts low-level press/release points into a real drag selection. The
/// distance threshold filters ordinary clicks before any native reader runs.
/// </summary>
public sealed class SelectionGestureDetector
{
    private readonly double _minimumHorizontalDistance;
    private readonly double _minimumVerticalDistance;
    private ScreenPoint _start;
    private bool _pressed;

    public SelectionGestureDetector(double minimumHorizontalDistance, double minimumVerticalDistance)
    {
        _minimumHorizontalDistance = Math.Max(1, minimumHorizontalDistance);
        _minimumVerticalDistance = Math.Max(1, minimumVerticalDistance);
    }

    public void Press(ScreenPoint point)
    {
        _start = point;
        _pressed = point.IsFinite;
    }

    public SelectionGesture? Release(ScreenPoint point, DateTimeOffset completedAt)
    {
        if (!_pressed)
        {
            return null;
        }

        _pressed = false;
        if (!point.IsFinite)
        {
            return null;
        }

        var horizontalDistance = Math.Abs(point.X - _start.X);
        var verticalDistance = Math.Abs(point.Y - _start.Y);
        if (horizontalDistance < _minimumHorizontalDistance
            && verticalDistance < _minimumVerticalDistance)
        {
            return null;
        }

        return new SelectionGesture(_start, point, completedAt);
    }

    public void Cancel() => _pressed = false;
}

public sealed record SelectionResult(
    string? Text,
    SelectionSource Source,
    SelectionBounds? Bounds = null,
    SelectionFailureKind Failure = SelectionFailureKind.None,
    string? DiagnosticCode = null)
{
    public bool Succeeded => !string.IsNullOrWhiteSpace(Text) && Failure == SelectionFailureKind.None;

    public static SelectionResult Failed(SelectionFailureKind failure, string? code = null) =>
        new(null, SelectionSource.Accessibility, null, failure, code);
}

public readonly record struct PermissionState(
    bool Accessibility,
    bool InputMonitoring,
    bool ClipboardFallback)
{
    public bool CanAttemptSelection => Accessibility || ClipboardFallback;
}

public interface ISelectionReader
{
    Task<SelectionResult> ReadAsync(
        SelectionRequest request,
        CancellationToken cancellationToken = default);
}

public interface IPlatformPermissionService
{
    Task<PermissionState> GetStateAsync(CancellationToken cancellationToken = default);

    Task OpenAccessibilitySettingsAsync(CancellationToken cancellationToken = default);
}
