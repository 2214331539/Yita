using System.Text;
using Yita.Core.Selection;
using Peer = Yita.MacHelperSmoke.Program;

namespace Yita.Native.Mac.Tests;

public sealed class MacHelperTests
{
    [Fact]
    public void HelperDiscoverySupportsSourceOutputAndInstalledBundles()
    {
        var root = Path.Combine(Path.GetTempPath(), "Yita package with spaces");
        var source = Path.Combine(root, "bin", "Release", "net8.0");
        var bundle = Path.Combine(root, "Yita.app", "Contents", "MacOS");
        var suffix = Path.Combine("Yita.Native.Mac.Helper.app", "Contents", "MacOS", "Yita.Native.Mac.Helper");
        Assert.Equal(Path.Combine(source, suffix), MacHelperClient.ResolveHelperPath(source));
        Assert.Equal(Path.Combine(root, "Yita.app", "Contents", "Helpers", suffix),
            MacHelperClient.ResolveHelperPath(bundle + Path.DirectorySeparatorChar));
        var overridePath = Path.Combine(root, "custom-helper");
        Assert.Equal(overridePath, MacHelperClient.ResolveHelperPath(bundle, overridePath));
        Assert.Equal(Path.Combine(root, "unrelated", "Contents", "MacOS", suffix),
            MacHelperClient.ResolveHelperPath(Path.Combine(root, "unrelated", "Contents", "MacOS")));
    }

    private static string RuntimeConfig => Path.ChangeExtension(typeof(MacHelperTests).Assembly.Location, ".runtimeconfig.json");
    private static MacHelperClient Client(string mode, TimeSpan? timeout = null) =>
        new(() => Peer.CreatePeerStartInfo(mode, RuntimeConfig), timeout);

    [Theory]
    [InlineData("wrong-version", NativeServiceState.ProtocolMismatch)]
    [InlineData("wrong-bundle", NativeServiceState.ProtocolMismatch)]
    [InlineData("wrong-pid", NativeServiceState.Unavailable)]
    [InlineData("wrong-id", NativeServiceState.Unavailable)]
    [InlineData("wrong-status", NativeServiceState.Unavailable)]
    [InlineData("invalid-json", NativeServiceState.Unavailable)]
    [InlineData("missing-permissions", NativeServiceState.Unavailable)]
    [InlineData("oversize", NativeServiceState.Unavailable)]
    [InlineData("crash", NativeServiceState.Unavailable)]
    public async Task UntrustedOrFailedHelperResponsesAreIsolated(string mode, NativeServiceState expected)
    {
        using var client = Client(mode);
        var result = await client.SendAsync("permissions");
        Assert.Equal(expected, result.State);
        Assert.Null(result.Response);
    }

    [Fact]
    public async Task PermissionsAndSelectionUseTheSameProcessAndContextRequiresOptIn()
    {
        var starts = 0;
        using var client = new MacHelperClient(() => { starts++; return Peer.CreatePeerStartInfo("selection", RuntimeConfig); });
        using var adapter = new MacSelectionAdapter(client);
        var status = await adapter.GetStatusAsync();
        Assert.True(status.Permissions.Accessibility);
        Assert.True(status.SelectionSupported);
        Assert.False(status.GlobalInputSupported);
        var request = new SelectionRequest(SelectionTrigger.TranslateShortcut, new ScreenPoint(10, 20));
        Assert.Null((await adapter.ReadAsync(request)).Context);
        var selection = await adapter.ReadAsync(request with { IncludeContext = true });
        Assert.Equal("selected text", selection.Text);
        Assert.Equal("private context", selection.Context);
        Assert.Equal(new SelectionBounds(10, 20, 100, 18), selection.Bounds);
        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task MissingHelperIsDifferentFromUnimplementedSelectionAndUnknownErrorTextIsNotExposed()
    {
        using var missing = new MacHelperClient(() => null);
        using var adapter = new MacSelectionAdapter(missing);
        Assert.Equal(NativeServiceState.Missing, (await adapter.GetStatusAsync()).Service);
        Assert.False((await adapter.GetStateAsync()).CanAttemptSelection);
        Assert.NotEqual(SelectionFailureKind.PermissionDenied, (await adapter.ReadAsync(
            new SelectionRequest(SelectionTrigger.MouseGesture, new ScreenPoint(1, 2)))).Failure);
        using var client = Client("private-error");
        var error = await client.SendAsync("permissions");
        Assert.Equal("mac-helper-operation-failed", error.DiagnosticCode);
        Assert.DoesNotContain("private", error.DiagnosticCode);
    }

    [Fact]
    public async Task TimeoutAndCallerCancellationStopTheWorkerAndAFreshRequestCanSucceed()
    {
        using var timeout = Client("hang", TimeSpan.FromMilliseconds(750));
        Assert.Equal(NativeServiceState.Timeout, (await timeout.SendAsync("permissions")).State);
        var starts = 0;
        using var client = new MacHelperClient(() => Peer.CreatePeerStartInfo(++starts == 1 ? "hang" : "normal", RuntimeConfig));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync("permissions", cancellationToken: cancel.Token));
        Assert.Equal(NativeServiceState.Available, (await client.SendAsync("permissions")).State);
        Assert.Equal(2, starts);
    }

    [Fact]
    public async Task RepeatedCrashesAreRateLimitedAndDisposalCancelsPendingRequests()
    {
        var time = new TestTimeProvider();
        var starts = 0;
        using var client = new MacHelperClient(() => Peer.CreatePeerStartInfo(++starts <= 3 ? "crash" : "normal", RuntimeConfig), time: time);
        for (var index = 0; index < 3; index++) Assert.Equal(NativeServiceState.Unavailable, (await client.SendAsync("permissions")).State);
        Assert.Equal(NativeServiceState.RestartBackoff, (await client.SendAsync("permissions")).State);
        Assert.Equal(3, starts);
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(NativeServiceState.Available, (await client.SendAsync("permissions")).State);
        using var pendingClient = Client("hang", TimeSpan.FromSeconds(10));
        var pending = pendingClient.SendAsync("permissions");
        var queued = pendingClient.SendAsync("permissions");
        await Task.Delay(100);
        pendingClient.Dispose();
        Assert.Equal(NativeServiceState.Unavailable, (await pending.WaitAsync(TimeSpan.FromSeconds(3))).State);
        Assert.Equal(NativeServiceState.Unavailable, (await queued.WaitAsync(TimeSpan.FromSeconds(3))).State);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pendingClient.SendAsync("permissions"));
    }

    [Fact]
    public async Task MissingHandshakeTimesOutAndWrongPlatformDoesNotStartAHelper()
    {
        using var client = Client("no-handshake", TimeSpan.FromMilliseconds(750));
        Assert.Equal(NativeServiceState.Timeout, (await client.SendAsync("permissions")).State);
        using var wrongPlatformClient = new MacHelperClient(() => throw new InvalidOperationException("Should not start"));
        using var adapter = new MacSelectionAdapter(wrongPlatformClient, () => false);
        Assert.Equal(NativeServiceState.NotSupported, (await adapter.GetStatusAsync()).Service);
        Assert.False((await adapter.GetStateAsync()).CanAttemptSelection);
    }

    [Fact]
    public async Task CancellationWhileQueuedDoesNotKillTheActiveRequestAndStderrCannotBlockTheHandshake()
    {
        using var client = Client("stderr-flood");
        var active = client.SendAsync("permissions");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync("permissions", cancellationToken: cancelled.Token));
        Assert.Equal(NativeServiceState.Available, (await active).State);
        Assert.Equal(NativeServiceState.Available, (await client.SendAsync("permissions")).State);
    }

    [Fact]
    public async Task BoundedReaderPreservesFollowingFramesAndRejectsTruncatedAndOversizedLines()
    {
        var reader = new BoundedJsonLineReader(new MemoryStream(Encoding.UTF8.GetBytes("123456\nsecond\n")), 6);
        Assert.Equal("123456", Encoding.UTF8.GetString(await reader.ReadAsync(default)));
        Assert.Equal("second", Encoding.UTF8.GetString(await reader.ReadAsync(default)));
        await Assert.ThrowsAsync<EndOfStreamException>(() => reader.ReadAsync(default));
        var truncated = new BoundedJsonLineReader(new MemoryStream(Encoding.UTF8.GetBytes("partial")), 8);
        await Assert.ThrowsAsync<EndOfStreamException>(() => truncated.ReadAsync(default));
        var oversized = new BoundedJsonLineReader(new MemoryStream(Encoding.UTF8.GetBytes("1234567")), 6);
        await Assert.ThrowsAsync<IOException>(() => oversized.ReadAsync(default));
    }

    [Theory]
    [InlineData("{\"id\":\"r\",\"status\":\"ok\"}")]
    [InlineData("{\"version\":2,\"id\":\"r\",\"status\":\"ok\",\"permissions\":{},\"capabilities\":{}}")]
    [InlineData("{\"version\":2,\"id\":\"r\",\"status\":\"ok\",\"selection\":{\"source\":99}}")]
    [InlineData("{\"version\":2,\"id\":\"r\",\"status\":\"ok\",\"selection\":{\"text\":\"private\",\"failure\":\"none\"}}")]
    [InlineData("{\"version\":2,\"id\":\"r\",\"status\":\"ok\",\"selection\":{\"source\":\"accessibility\"}}")]
    [InlineData("{\"version\":2,\"id\":\"r\",\"status\":\"ok\",\"permissions\":{\"accessibility\":true,\"inputMonitoring\":false}}")]
    [InlineData("null")]
    [InlineData("[]")]
    public void MissingVersionPermissionFieldsAndUnknownEnumValuesAreRejected(string json)
    {
        Assert.Throws<MacHelperProtocolException>(() => MacHelperProtocol.Parse(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void MissingSelectionInvalidBoundsAndManualClipboardSourcesAreRejected()
    {
        var response = new MacHelperResponse { Version = MacHelperProtocol.Version, Id = "request", Status = "ok" };
        Assert.Throws<MacHelperProtocolException>(() => MacHelperProtocol.ValidateResponse(response, "request", "readSelection"));
        foreach (var selection in new[]
        {
            new SelectionResult("text", SelectionSource.Accessibility, new SelectionBounds(0, 0, -1, 10)),
            new SelectionResult("text", SelectionSource.ManualClipboard),
            new SelectionResult(new string('x', 20_001), SelectionSource.Accessibility),
            new SelectionResult("private", SelectionSource.Accessibility, Failure: SelectionFailureKind.ProtectedContent),
            new SelectionResult(null, SelectionSource.Accessibility, Failure: SelectionFailureKind.Cancelled, Context: "private"),
        })
            Assert.Throws<MacHelperProtocolException>(() => MacHelperProtocol.ValidateResponse(response with { Selection = selection }, "request", "readSelection"));
    }

    [Theory]
    [InlineData(SelectionFailureKind.Empty, "ax-empty")]
    [InlineData(SelectionFailureKind.PermissionDenied, "ax-permission-denied")]
    [InlineData(SelectionFailureKind.ProtectedContent, "ax-protected-content")]
    [InlineData(SelectionFailureKind.Cancelled, "ax-target-changed")]
    [InlineData(SelectionFailureKind.Timeout, "ax-timeout")]
    [InlineData(SelectionFailureKind.UnsupportedApplication, "ax-unsupported")]
    public async Task NativeSelectionFailuresStayTypedAndSourceIdentityIsForwarded(SelectionFailureKind failure, string diagnostic)
    {
        using var helper = new SelectionHelper(SelectionResult.Failed(failure, diagnostic));
        using var adapter = new MacSelectionAdapter(helper);
        var request = new SelectionRequest(SelectionTrigger.MouseGesture, new ScreenPoint(-500, 120),
            ForegroundApplication: "test.editor", ForegroundProcessId: 42);
        var result = await adapter.ReadAsync(request);
        Assert.Equal(failure, result.Failure);
        Assert.Equal("mac-helper-" + diagnostic, result.DiagnosticCode);
        Assert.Null(result.Text);
        Assert.Null(result.Context);
        Assert.Equal(request, helper.Request);
        Assert.Equal(42, helper.Request!.ForegroundProcessId);
    }

    [Fact]
    public async Task InvalidSourceProcessAndCoordinatesNeverReachTheHelper()
    {
        using var helper = new SelectionHelper(new SelectionResult("text", SelectionSource.Accessibility));
        using var adapter = new MacSelectionAdapter(helper);
        var request = new SelectionRequest(SelectionTrigger.MouseGesture, new ScreenPoint(0, 0), ForegroundProcessId: 0);
        Assert.False((await adapter.ReadAsync(request)).Succeeded);
        Assert.False((await adapter.ReadAsync(request with { ForegroundProcessId = 42, Pointer = new ScreenPoint(double.NaN, 0) })).Succeeded);
        Assert.Null(helper.Request);
    }

    [Theory]
    [InlineData("copy-pending")]
    [InlineData("copy-partial")]
    public async Task ClipboardCancellationDrainsTheOriginalFrameAndReusesTheCleanHelper(string mode)
    {
        var starts = 0;
        using var client = new MacHelperClient(() => { starts++; return Peer.CreatePeerStartInfo(mode, RuntimeConfig); });
        Assert.Equal(NativeServiceState.Available, (await client.SendAsync("permissions")).State);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var request = new SelectionRequest(SelectionTrigger.MouseGesture, new ScreenPoint(10, 20));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync("readSelection", request, cancel.Token, allowClipboardFallback: true));
        Assert.Equal(NativeServiceState.Available, (await client.SendAsync("permissions")).State);
        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task ClipboardTimeoutWaitsForCancellationCleanupBeforeTheNextRequest()
    {
        using var client = Client("copy-pending", TimeSpan.FromMilliseconds(800));
        Assert.Equal(NativeServiceState.Available, (await client.SendAsync("permissions")).State);
        var result = await client.SendAsync("readSelection", new(SelectionTrigger.MouseGesture, new ScreenPoint(10, 20)), allowClipboardFallback: true);
        Assert.Equal(NativeServiceState.Timeout, result.State);
        Assert.Equal(NativeServiceState.Available, (await client.SendAsync("permissions")).State);
    }

    [Fact]
    public async Task DisposalDuringClipboardWorkSignalsEOFAndCompletesPendingCalls()
    {
        using var client = Client("copy-pending");
        Assert.Equal(NativeServiceState.Available, (await client.SendAsync("permissions")).State);
        var pending = client.SendAsync("readSelection", new(SelectionTrigger.MouseGesture, new ScreenPoint(10, 20)), allowClipboardFallback: true);
        await Task.Delay(50);
        client.Dispose();
        Assert.Equal(NativeServiceState.Unavailable, (await pending.WaitAsync(TimeSpan.FromSeconds(3))).State);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.SendAsync("permissions"));
    }

    [Fact]
    public async Task AdapterRequiresExplicitClipboardFallbackOptIn()
    {
        using var helper = new SelectionHelper(new SelectionResult("copied text", SelectionSource.ClipboardFallback));
        using var adapter = new MacSelectionAdapter(helper);
        var request = new SelectionRequest(SelectionTrigger.MouseGesture, new ScreenPoint(10, 20));
        Assert.False((await adapter.ReadAsync(request)).Succeeded);
        Assert.False(helper.ClipboardAllowed);
        Assert.True((await adapter.ReadAsync(request, allowClipboardFallback: true)).Succeeded);
        Assert.True(helper.ClipboardAllowed);
    }

    [Fact]
    public void ClipboardResponsesCannotClaimContextOrUseAnUnrequestedCopy()
    {
        var response = new MacHelperResponse
        {
            Version = MacHelperProtocol.Version, Id = "request", Status = "ok",
            Selection = new SelectionResult("text", SelectionSource.ClipboardFallback),
        };
        Assert.Throws<MacHelperProtocolException>(() => MacHelperProtocol.ValidateResponse(response, "request", "readSelection"));
        MacHelperProtocol.ValidateResponse(response, "request", "readSelection", allowClipboardFallback: true);
        Assert.Throws<MacHelperProtocolException>(() => MacHelperProtocol.ValidateResponse(response with
        { Selection = response.Selection with { Context = "unrelated" } }, "request", "readSelection", allowClipboardFallback: true));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task ClipboardPermissionRequiresEventPosting(bool posting, bool expected)
    {
        using var helper = new PermissionHelper(posting);
        using var adapter = new MacSelectionAdapter(helper);
        Assert.Equal(expected, (await adapter.GetStatusAsync()).Permissions.ClipboardFallback);
    }

    private sealed class PermissionHelper(bool posting) : IMacHelperClient
    {
        public Task<MacHelperExchange> SendAsync(string command, SelectionRequest? selection = null,
            CancellationToken cancellationToken = default, bool allowClipboardFallback = false, MacInputOptions? input = null) => Task.FromResult(new MacHelperExchange(
                NativeServiceState.Available, new MacHelperResponse
                {
                    Version = MacHelperProtocol.Version, Id = "test", Status = "ok",
                    Permissions = new MacHelperPermissions { Accessibility = true, InputMonitoring = false, EventPosting = posting },
                    Capabilities = new MacHelperCapabilities { Selection = true, ClipboardFallback = true, GlobalInput = false },
                }));
        public void Dispose() { }
    }

    private sealed class SelectionHelper(SelectionResult result) : IMacHelperClient
    {
        public SelectionRequest? Request { get; private set; }
        public bool ClipboardAllowed { get; private set; }
        public Task<MacHelperExchange> SendAsync(string command, SelectionRequest? selection = null, CancellationToken cancellationToken = default,
            bool allowClipboardFallback = false, MacInputOptions? input = null)
        {
            Request = selection;
            ClipboardAllowed = allowClipboardFallback;
            return Task.FromResult(new MacHelperExchange(NativeServiceState.Available,
                new MacHelperResponse { Version = MacHelperProtocol.Version, Id = "test", Status = "ok", Selection = result }));
        }
        public void Dispose() { }
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _timestamp;
        internal void Advance(TimeSpan duration) => _timestamp += (long)duration.TotalMilliseconds;
    }
}
