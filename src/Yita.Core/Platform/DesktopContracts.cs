using Yita.Core.Selection;

namespace Yita.Core.Platform;

public interface ISelectionRuntime : IDisposable
{
    event EventHandler<SelectionCapturedEventArgs>? SelectionCaptured;
    event EventHandler<ScreenPoint>? ExternalPointerPressed;
    bool IsRunning { get; }
    bool IsHotkeyRunning { get; }
    void Start();
    void Configure(bool isEnabled, bool useClipboardFallback, int selectionDelayMilliseconds,
        bool useWpsPdfCompatibility = true, bool useSelectionContext = false);
    void TranslateClipboard();
    void RepairInputCapture();
    string CreateDiagnostics(bool chinese);
}

public sealed class SelectionCapturedEventArgs(SelectionRequest request, SelectionResult result, TimeSpan? readDuration = null) : EventArgs
{
    public SelectionRequest Request { get; } = request;
    public SelectionResult Result { get; } = result;
    public TimeSpan? ReadDuration { get; } = readDuration;
}

public interface ISingleInstanceGuard : IDisposable
{
    bool IsOwner { get; }
    void StartActivationListener(Action openSettings);
}

public interface IDesktopSessionRuntime
{
    bool IsSessionActive { get; }
    event EventHandler<bool>? SessionActivityChanged;
}

public interface IStartupRegistration
{
    bool IsSupported { get; }
    void Apply(bool enabled);
}

public interface IStatusIcon : IDisposable
{
    event EventHandler<ScreenPoint>? MenuRequested;
    event EventHandler? OpenRequested;
    bool IsRunning { get; }
}

public sealed class UnsupportedStartupRegistration : IStartupRegistration
{
    public bool IsSupported => false;
    public void Apply(bool enabled)
    {
        if (enabled) throw new PlatformNotSupportedException("Login startup is not available on this platform yet.");
    }
}
