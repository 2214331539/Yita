using Yita.Core.Selection;

namespace Yita.Native.Mac;

/// <summary>
/// Contract boundary for the macOS AXUIElement implementation. The actual
/// Accessibility calls will live in a signed Swift helper so that AX errors
/// cannot terminate the Avalonia process.
/// </summary>
public sealed class MacSelectionAdapter : ISelectionReader, IPlatformPermissionService
{
    public Task<SelectionResult> ReadAsync(
        SelectionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsMacOS())
            return Task.FromResult(SelectionResult.Failed(SelectionFailureKind.UnsupportedApplication, "macos-only"));

        return Task.FromResult(SelectionResult.Failed(
            SelectionFailureKind.PermissionDenied,
            "mac-accessibility-adapter-not-installed"));
    }

    public Task<PermissionState> GetStateAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new PermissionState(
            Accessibility: OperatingSystem.IsMacOS() && false,
            InputMonitoring: OperatingSystem.IsMacOS() && false,
            ClipboardFallback: OperatingSystem.IsMacOS()));

    public Task OpenAccessibilitySettingsAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsMacOS()) return Task.CompletedTask;
        return Task.CompletedTask;
    }
}
