using Yita.Core.Selection;

namespace Yita.Native.Mac;

/// <summary>
/// Reads permissions and selections through the isolated native helper.
/// </summary>
public sealed class MacSelectionAdapter : ISelectionReader, IPlatformPermissionService, IDisposable
{
    private readonly IMacHelperClient _helper;
    private readonly Func<bool> _isMac;

    public MacSelectionAdapter() : this(new MacHelperClient(), OperatingSystem.IsMacOS) { }
    internal MacSelectionAdapter(IMacHelperClient helper, Func<bool>? isMac = null)
    { _helper = helper; _isMac = isMac ?? (() => true); }

    public Task<SelectionResult> ReadAsync(
        SelectionRequest request,
        CancellationToken cancellationToken = default) => ReadAsync(request, false, cancellationToken);

    public async Task<SelectionResult> ReadAsync(SelectionRequest request, bool allowClipboardFallback,
        CancellationToken cancellationToken = default) =>
        await ReadForInputAsync(request, allowClipboardFallback, null, cancellationToken).ConfigureAwait(false);

    internal async Task<SelectionResult> ReadForInputAsync(SelectionRequest request, bool allowClipboardFallback,
        long? sequence, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_isMac()) return SelectionResult.Failed(SelectionFailureKind.UnsupportedApplication, "macos-only");
        if (!request.Pointer.IsFinite || request.GestureBounds is { IsValid: false }
            || request.ForegroundProcessId is <= 1)
            return SelectionResult.Failed(SelectionFailureKind.Unknown, "mac-invalid-selection-coordinates");
        var result = await _helper.SendAsync("readSelection", request, cancellationToken, allowClipboardFallback,
            sequence is { } value ? new MacInputOptions(Sequence: value) : null).ConfigureAwait(false);
        if (result.State != NativeServiceState.Available)
            return SelectionResult.Failed(result.State == NativeServiceState.Timeout ? SelectionFailureKind.Timeout
                : SelectionFailureKind.Unknown, result.DiagnosticCode);
        if (result.Response?.Status == "ok" && result.Response.Selection is { } selection)
        {
            if (!allowClipboardFallback && selection.Source == SelectionSource.ClipboardFallback)
                return SelectionResult.Failed(SelectionFailureKind.Unknown, "mac-helper-unrequested-clipboard-copy");
            return selection with
            {
                Context = request.IncludeContext ? selection.Context : null,
                Failure = selection.Failure == SelectionFailureKind.None && string.IsNullOrWhiteSpace(selection.Text)
                    ? SelectionFailureKind.Empty : selection.Failure,
                DiagnosticCode = selection.DiagnosticCode is null ? null : MacHelperProtocol.SafeDiagnostic(selection.DiagnosticCode),
            };
        }
        return SelectionResult.Failed(result.DiagnosticCode == "mac-helper-permission-denied"
            ? SelectionFailureKind.PermissionDenied : SelectionFailureKind.UnsupportedApplication, result.DiagnosticCode);
    }

    public async Task<PermissionState> GetStateAsync(CancellationToken cancellationToken = default) =>
        (await GetStatusAsync(cancellationToken).ConfigureAwait(false)).Permissions;

    public async Task<PlatformPermissionStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_isMac()) return new(NativeServiceState.NotSupported, default, DiagnosticCode: "macos-only");
        var result = await _helper.SendAsync("permissions", cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result is { State: NativeServiceState.Available, Response.Status: "ok", Response.Permissions: { } permissions,
            Response.Capabilities: { } capabilities })
            return new(result.State, new PermissionState(permissions.Accessibility, permissions.InputMonitoring,
                capabilities.ClipboardFallback && permissions.Accessibility && permissions.EventPosting), capabilities.Selection, capabilities.GlobalInput);
        return new(result.State == NativeServiceState.Available ? NativeServiceState.Unavailable : result.State,
            default, DiagnosticCode: result.DiagnosticCode);
    }

    public Task RequestAccessibilityPermissionAsync(CancellationToken cancellationToken = default) =>
        ExecuteActionAsync("requestAccessibility", cancellationToken);

    public Task OpenAccessibilitySettingsAsync(CancellationToken cancellationToken = default) =>
        ExecuteActionAsync("openAccessibilitySettings", cancellationToken);

    public Task RequestInputMonitoringPermissionAsync(CancellationToken cancellationToken = default) =>
        ExecuteActionAsync("requestInputMonitoring", cancellationToken);

    public Task OpenInputMonitoringSettingsAsync(CancellationToken cancellationToken = default) =>
        ExecuteActionAsync("openInputMonitoringSettings", cancellationToken);

    private async Task ExecuteActionAsync(string command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_isMac()) throw new PlatformNotSupportedException("macOS permissions are unavailable.");
        var result = await _helper.SendAsync(command, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.State != NativeServiceState.Available || result.Response?.Status != "ok")
            throw new IOException("The macOS permission helper could not complete this action.");
    }

    public void Dispose() => _helper.Dispose();
}
