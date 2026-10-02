using System.Text;
using Yita.Core.Selection;
using Peer = Yita.MacHelperSmoke.Program;

namespace Yita.Native.Mac.Tests;

public sealed class MacHelperTests
{
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
    [InlineData("{\"version\":1,\"id\":\"r\",\"status\":\"ok\",\"permissions\":{},\"capabilities\":{}}")]
    [InlineData("{\"version\":1,\"id\":\"r\",\"status\":\"ok\",\"selection\":{\"source\":99}}")]
    public void MissingVersionPermissionFieldsAndUnknownEnumValuesAreRejected(string json)
    {
        Assert.Throws<MacHelperProtocolException>(() => MacHelperProtocol.Parse(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void MissingSelectionInvalidBoundsAndManualClipboardSourcesAreRejected()
    {
        var response = new MacHelperResponse { Version = 1, Id = "request", Status = "ok" };
        Assert.Throws<MacHelperProtocolException>(() => MacHelperProtocol.ValidateResponse(response, "request", "readSelection"));
        foreach (var selection in new[]
        {
            new SelectionResult("text", SelectionSource.Accessibility, new SelectionBounds(0, 0, -1, 10)),
            new SelectionResult("text", SelectionSource.ManualClipboard),
            new SelectionResult(new string('x', 20_001), SelectionSource.Accessibility),
        })
            Assert.Throws<MacHelperProtocolException>(() => MacHelperProtocol.ValidateResponse(response with { Selection = selection }, "request", "readSelection"));
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _timestamp;
        internal void Advance(TimeSpan duration) => _timestamp += (long)duration.TotalMilliseconds;
    }
}
