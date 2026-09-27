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
    string? ForegroundApplication = null);

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
