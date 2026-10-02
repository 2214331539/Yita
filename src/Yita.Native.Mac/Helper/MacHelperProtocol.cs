using System.Text.Json;
using System.Text.Json.Serialization;
using Yita.Core.Selection;

namespace Yita.Native.Mac;

internal static class MacHelperProtocol
{
    internal const int Version = 1;
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

    internal static void ValidateResponse(MacHelperResponse response, string id, string command)
    {
        if (response.Version != Version) throw new MacHelperProtocolException(NativeServiceState.ProtocolMismatch);
        if (response.Id != id || response.Status is not ("ok" or "error"))
            throw new MacHelperProtocolException(NativeServiceState.Unavailable);
        if (response.Status == "ok" && command is "permissions" or "requestAccessibility"
            && (response.Permissions is null || response.Capabilities is null))
            throw new MacHelperProtocolException(NativeServiceState.Unavailable);
        if (response.Status == "ok" && command == "readSelection" && response.Selection is null)
            throw new MacHelperProtocolException(NativeServiceState.Unavailable);
        if (response.Selection is { } selection &&
            (selection.Text?.Length > 20_000 || selection.Context?.Length > 20_000
                || selection.Bounds is { IsValid: false }
                || (selection.Failure != SelectionFailureKind.None
                    && (selection.Text is not null || selection.Context is not null || selection.Bounds is not null))
                || selection.Source == SelectionSource.ManualClipboard))
            throw new MacHelperProtocolException(NativeServiceState.Unavailable);
    }

    internal static string SafeDiagnostic(string? code) => code switch
    {
        "selection-not-implemented" or "permission-denied" or "open-settings-failed"
            or "ax-permission-denied" or "ax-target-unavailable" or "ax-target-changed"
            or "ax-protected-content" or "ax-timeout" or "ax-unsupported" or "ax-text-limit"
            or "ax-unavailable" or "ax-empty" or "invalid-selection-request"
            or "unsupported-command" or "self-test-action-disabled" => "mac-helper-" + code,
        _ => "mac-helper-operation-failed",
    };
}

internal sealed record MacHelperRequest(int Version, string Id, string Command, SelectionRequest? Selection = null);
internal sealed record MacHelperPermissions
{
    public required bool Accessibility { get; init; }
    public required bool InputMonitoring { get; init; }
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
}
internal sealed record MacHelperExchange(NativeServiceState State, MacHelperResponse? Response = null, string? DiagnosticCode = null);
internal sealed class MacHelperProtocolException(NativeServiceState state) : IOException("Invalid native helper protocol.")
{
    internal NativeServiceState State { get; } = state;
}
internal interface IMacHelperClient : IDisposable
{
    Task<MacHelperExchange> SendAsync(string command, SelectionRequest? selection = null, CancellationToken cancellationToken = default);
}
