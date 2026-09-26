using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Yita.Services;

namespace Yita.Tests;

public sealed class ZoteroSelectionBridgeTests
{
    [Fact]
    public async Task AuthorizedSelectionIsAcceptedAndRaisedWithoutRetention()
    {
        using var bridge = new ZoteroSelectionBridge(port: 0);
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        bridge.SelectionReceived += text => received.TrySetResult(text);
        bridge.Start();
        using var client = CreateAuthorizedClient(bridge.Port);

        var response = await client.PostAsJsonAsync("v1/selection", new { text = "  selected text  " });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("selected text", await received.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, bridge.AcceptedCount);
        Assert.DoesNotContain("selected text", bridge.CreateStatusReport(useChinese: false));
    }

    [Fact]
    public async Task MissingBridgeHeaderIsRejected()
    {
        using var bridge = new ZoteroSelectionBridge(port: 0);
        var raised = false;
        bridge.SelectionReceived += _ => raised = true;
        bridge.Start();
        using var client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{bridge.Port}/"),
        };

        var response = await client.PostAsJsonAsync("v1/selection", new { text = "private" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(raised);
        Assert.Equal(1, bridge.RejectedCount);
    }

    [Fact]
    public async Task HealthEndpointDoesNotRaiseSelection()
    {
        using var bridge = new ZoteroSelectionBridge(port: 0);
        var raised = false;
        bridge.SelectionReceived += _ => raised = true;
        bridge.Start();
        using var client = CreateAuthorizedClient(bridge.Port);

        var response = await client.GetAsync("health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(raised);
        Assert.Equal(0, bridge.AcceptedCount);
    }

    [Fact]
    public async Task OversizedSelectionIsRejected()
    {
        using var bridge = new ZoteroSelectionBridge(port: 0);
        bridge.Start();
        using var client = CreateAuthorizedClient(bridge.Port);

        var response = await client.PostAsJsonAsync("v1/selection", new { text = new string('x', 20_001) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1, bridge.RejectedCount);
    }

    private static HttpClient CreateAuthorizedClient(int port)
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
        };
        client.DefaultRequestHeaders.Add(
            ZoteroSelectionBridge.BridgeHeaderName,
            ZoteroSelectionBridge.BridgeHeaderValue);
        return client;
    }
}
