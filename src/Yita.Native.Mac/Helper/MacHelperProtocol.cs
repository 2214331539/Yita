using System.Text.Json;
using System.Text.Json.Serialization;
using Yita.Core.Selection;

namespace Yita.Native.Mac;

internal static class MacHelperProtocol
{
    internal const int Version = 3;
    internal const string BundleIdentifier = "com.yita.desktop.native-helper";
    internal const int MaximumRequestBytes = 64_000;
    internal const int MaximumResponseBytes = 256_000;
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    internal static MacHelperResponse Parse(byte[] data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new MacHelperProtocolException(NativeServiceState.Unavailable);
            if (document.RootElement.TryGetProperty("selection", out var selection) && selection.ValueKind != JsonValueKind.Null
                && (selection.ValueKind != JsonValueKind.Object || !selection.TryGetProperty("source", out _)
                    || !selection.TryGetProperty("failure", out _)))
                throw new MacHelperProtocolException(NativeServiceState.Unavailable);
            return JsonSerializer.Deserialize<MacHelperResponse>(data, JsonOptions)
                ?? throw new MacHelperProtocolException(NativeServiceState.Unavailable);
        }
        catch (JsonException) { throw new MacHelperProtocolException(NativeServiceState.Unavailable); }
    }

    internal static void ValidateReady(MacHelperResponse response, int processId)
    {
        if (response.Version != Version || response.BundleIdentifier != BundleIdentifier)
            throw new MacHelperProtocolException(NativeServiceState.ProtocolMismatch);
        if (response.Id != "ready" || response.Status != "ready" || response.ProcessId != processId)
            throw new MacHelperProtocolException(NativeServiceState.Unavailable);
    }

    internal static void ValidateResponse(MacHelperResponse response, string id, string command, bool allowClipboardFallback = false)
    {
        if (response.Version != Version) throw new MacHelperProtocolException(NativeServiceState.ProtocolMismatch);
        if (response.Id != id || response.Status is not ("ok" or "error"))
            throw new MacHelperProtocolException(NativeServiceState.Unavailable);
        if (response.Pointer is { } pointer && (!pointer.IsFinite || Math.Abs(pointer.X) > 10_000_000 || Math.Abs(pointer.Y) > 10_000_000))
            throw new MacHelperProtocolException(NativeServiceState.Unavailable);
        if (response.Status == "ok" && command is "permissions" or "requestAccessibility" or "requestInputMonitoring"
            && (response.Permissions is null || response.Capabilities is null))
            throw new MacHelperProtocolException(NativeServiceState.Unavailable);
        if (response.Status == "ok" && command is "readSelection" or "readClipboard" && response.Selection is null)
            throw new MacHelperProtocolException(NativeServiceState.Unavailable);
        if (response.Status == "ok" && command is "configureInput" or "pollInput" && response.Input is null)
            throw new MacHelperProtocolException(NativeServiceState.Unavailable);
        if (response.Input is { } input)
        {
            if (input.Sequence < 0 || input.Events is null || input.Events.Length > 64)
                throw new MacHelperProtocolException(NativeServiceState.Unavailable);
            long previous = 0;
            foreach (var item in input.Events)
            {
                if (item.Sequence <= previous || item.Sequence > input.Sequence
                    || !item.Pointer.IsFinite || Math.Abs(item.Pointer.X) > 10_000_000 || Math.Abs(item.Pointer.Y) > 10_000_000
                    || !double.IsFinite(item.AgeMilliseconds) || item.AgeMilliseconds is < 0 or > 500
                    || item.ForegroundProcessId is <= 1 || item.ForegroundApplication?.Length > 255
                    || (item.Kind is MacInputKind.PointerDown or MacInputKind.PointerUp && item.ForegroundProcessId is null))
                    throw new MacHelperProtocolException(NativeServiceState.Unavailable);
                previous = item.Sequence;
            }
        }
        if (response.Selection is { } selection &&
            (selection.Text?.Length > 20_000 || selection.Context?.Length > 20_000
                || selection.Bounds is { IsValid: false }
                || (selection.Failure != SelectionFailureKind.None
                    && (selection.Text is not null || selection.Context is not null || selection.Bounds is not null))
                || (selection.Source == SelectionSource.ClipboardFallback && (!allowClipboardFallback || selection.Context is not null || selection.Bounds is not null))
                || (selection.Source == SelectionSource.ManualClipboard && command != "readClipboard")
                || (command == "readClipboard" && (selection.Source != SelectionSource.ManualClipboard || selection.Context is not null || selection.Bounds is not null))))
            throw new MacHelperProtocolException(NativeServiceState.Unavailable);
    }

    internal static string SafeDiagnostic(string? code) => code switch
    {
        "selection-not-implemented" or "permission-denied" or "open-settings-failed"
            or "ax-permission-denied" or "ax-target-unavailable" or "ax-target-changed"
            or "ax-protected-content" or "ax-timeout" or "ax-unsupported" or "ax-text-limit"
            or "ax-unavailable" or "ax-empty" or "invalid-selection-request"
            or "selection-cancelled" or "clipboard-permission-denied" or "clipboard-unsafe-target"
            or "clipboard-snapshot-unavailable" or "clipboard-superseded" or "clipboard-user-input"
            or "clipboard-copy-timeout" or "clipboard-no-text" or "clipboard-text-limit" or "clipboard-restore-failed"
            or "input-monitoring-denied" or "input-tap-unavailable" or "hotkey-conflict" or "input-not-configured"
            or "unsupported-command" or "self-test-action-disabled" => "mac-helper-" + code,
        _ => "mac-helper-operation-failed",
    };
}

internal sealed record MacHelperRequest(int Version, string Id, string Command, SelectionRequest? Selection = null,
    bool AllowClipboardFallback = false, MacInputOptions? Input = null);
internal sealed record MacInputOptions(bool MouseEnabled = false, long? Sequence = null);
internal enum MacInputKind { PointerDown, PointerUp, Cancel, TranslateClipboard }
internal sealed record MacInputEvent
{
    public required MacInputKind Kind { get; init; }
    public required long Sequence { get; init; }
    public required ScreenPoint Pointer { get; init; }
    public required double AgeMilliseconds { get; init; }
    public int? ForegroundProcessId { get; init; }
    public string? ForegroundApplication { get; init; }
    public bool Modified { get; init; }
}
internal sealed record MacInputSnapshot
{
    public required bool MouseRunning { get; init; }
    public required bool HotkeyRunning { get; init; }
    public required long Sequence { get; init; }
    public required MacInputEvent[] Events { get; init; }
}
internal sealed record MacHelperPermissions
{
    public required bool Accessibility { get; init; }
    public required bool InputMonitoring { get; init; }
    public required bool EventPosting { get; init; }
}
internal sealed record MacHelperCapabilities
{
    public required bool Selection { get; init; }
    public required bool ClipboardFallback { get; init; }
    public required bool GlobalInput { get; init; }
}
internal sealed record MacHelperResponse
{
    public required int Version { get; init; }
    public required string Id { get; init; }
    public required string Status { get; init; }
    public string? BundleIdentifier { get; init; }
    public int? ProcessId { get; init; }
    public MacHelperPermissions? Permissions { get; init; }
    public MacHelperCapabilities? Capabilities { get; init; }
    public SelectionResult? Selection { get; init; }
    public string? DiagnosticCode { get; init; }
    public MacInputSnapshot? Input { get; init; }
    public ScreenPoint? Pointer { get; init; }
}
internal sealed record MacHelperExchange(NativeServiceState State, MacHelperResponse? Response = null, string? DiagnosticCode = null);
internal sealed class MacHelperProtocolException(NativeServiceState state) : IOException("Invalid native helper protocol.")
{
    internal NativeServiceState State { get; } = state;
}
internal interface IMacHelperClient : IDisposable
{
    Task<MacHelperExchange> SendAsync(string command, SelectionRequest? selection = null, CancellationToken cancellationToken = default,
        bool allowClipboardFallback = false, MacInputOptions? input = null);
}
