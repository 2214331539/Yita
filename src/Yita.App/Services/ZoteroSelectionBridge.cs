using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Yita.Selection;

namespace Yita.Services;

/// <summary>
/// Receives the current Zotero PDF-reader selection from the companion plugin.
/// The listener is loopback-only, accepts one small JSON shape, and never logs
/// or retains request text.
/// </summary>
internal sealed class ZoteroSelectionBridge : IDisposable
{
    internal const int DefaultPort = 38473;
    internal const string BridgeHeaderName = "X-Yita-Bridge";
    internal const string BridgeHeaderValue = "zotero-reader-v1";
    private const int MaximumHeaderBytes = 16 * 1024;
    private const int MaximumBodyBytes = 64 * 1024;
    private const int MaximumTextLength = 20_000;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _acceptLoop;
    private int _started;
    private long _acceptedCount;
    private long _rejectedCount;

    public ZoteroSelectionBridge(int port = DefaultPort)
    {
        if (port is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public event Action<string>? SelectionReceived;

    internal int Port => (_listener.LocalEndpoint as IPEndPoint)?.Port ?? 0;

    internal bool IsRunning => Volatile.Read(ref _started) == 1 && !_shutdown.IsCancellationRequested;

    internal long AcceptedCount => Interlocked.Read(ref _acceptedCount);

    internal long RejectedCount => Interlocked.Read(ref _rejectedCount);

    public void Start()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            return;
        }

        try
        {
            _listener.Start(backlog: 4);
            _acceptLoop = AcceptLoopAsync(_shutdown.Token);
        }
        catch
        {
            Interlocked.Exchange(ref _started, 0);
            throw;
        }
    }

    public string CreateStatusReport(bool useChinese)
    {
        return useChinese
            ? string.Join(
                Environment.NewLine,
                "Zotero PDF 桥接状态",
                $"监听：{(IsRunning ? "正常" : "不可用")}",
                $"已接收：{AcceptedCount}",
                $"已拒绝：{RejectedCount}")
            : string.Join(
                Environment.NewLine,
                "Zotero PDF bridge status",
                $"Listener: {(IsRunning ? "running" : "unavailable")}",
                $"Accepted: {AcceptedCount}",
                $"Rejected: {RejectedCount}");
    }

    public void Dispose()
    {
        if (!_shutdown.IsCancellationRequested)
        {
            _shutdown.Cancel();
        }

        _listener.Stop();
        try
        {
            _acceptLoop?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException exception)
            when (exception.InnerExceptions.All(item => item is OperationCanceledException or SocketException))
        {
        }

        _shutdown.Dispose();
        Interlocked.Exchange(ref _started, 0);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            _ = HandleClientSafelyAsync(client, cancellationToken);
        }
    }

    private async Task HandleClientSafelyAsync(TcpClient client, CancellationToken shutdownToken)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken))
        {
            timeout.CancelAfter(RequestTimeout);
            try
            {
                await HandleClientAsync(client, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _rejectedCount);
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                Interlocked.Increment(ref _rejectedCount);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var stream = client.GetStream();
        var headerBytes = await ReadHeadersAsync(stream, cancellationToken).ConfigureAwait(false);
        if (headerBytes is null)
        {
            Interlocked.Increment(ref _rejectedCount);
            await WriteResponseAsync(stream, 431, "Request Header Fields Too Large", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var headerText = Encoding.ASCII.GetString(headerBytes);
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0)
        {
            await RejectAsync(stream, 400, "Bad Request", cancellationToken).ConfigureAwait(false);
            return;
        }

        var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length < 2)
        {
            await RejectAsync(stream, 400, "Bad Request", cancellationToken).ConfigureAwait(false);
            return;
        }

        var headers = ParseHeaders(lines.Skip(1));
        if (!headers.TryGetValue(BridgeHeaderName, out var bridgeHeader)
            || !string.Equals(bridgeHeader, BridgeHeaderValue, StringComparison.Ordinal))
        {
            await RejectAsync(stream, 401, "Unauthorized", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(requestLine[0], "GET", StringComparison.OrdinalIgnoreCase)
            && string.Equals(requestLine[1], "/health", StringComparison.Ordinal))
        {
            await WriteResponseAsync(stream, 200, "OK", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!string.Equals(requestLine[0], "POST", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(requestLine[1], "/v1/selection", StringComparison.Ordinal))
        {
            await RejectAsync(stream, 404, "Not Found", cancellationToken).ConfigureAwait(false);
            return;
        }

        byte[]? body = null;
        if (headers.TryGetValue("Content-Length", out var contentLengthText)
            && int.TryParse(contentLengthText, out var contentLength)
            && contentLength > 0)
        {
            if (contentLength > MaximumBodyBytes)
            {
                await RejectAsync(stream, 413, "Payload Too Large", cancellationToken).ConfigureAwait(false);
                return;
            }

            body = new byte[contentLength];
            await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        }
        else if (headers.TryGetValue("Transfer-Encoding", out var transferEncoding)
                 && transferEncoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            body = await ReadChunkedBodyAsync(stream, cancellationToken).ConfigureAwait(false);
            if (body is null)
            {
                await RejectAsync(stream, 413, "Payload Too Large", cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        if (body is null or { Length: 0 })
        {
            await RejectAsync(stream, 400, "Bad Request", cancellationToken).ConfigureAwait(false);
            return;
        }

        ZoteroSelectionPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<ZoteroSelectionPayload>(body);
        }
        catch (JsonException)
        {
            await RejectAsync(stream, 400, "Bad Request", cancellationToken).ConfigureAwait(false);
            return;
        }

        var text = TextNormalizer.Normalize(payload?.Text);
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumTextLength)
        {
            await RejectAsync(stream, 400, "Bad Request", cancellationToken).ConfigureAwait(false);
            return;
        }

        Interlocked.Increment(ref _acceptedCount);
        SelectionReceived?.Invoke(text);
        await WriteResponseAsync(stream, 202, "Accepted", cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]?> ReadHeadersAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(512);
        var matched = 0;
        var terminator = new byte[] { 13, 10, 13, 10 };
        var oneByte = new byte[1];
        while (bytes.Count < MaximumHeaderBytes)
        {
            var count = await stream.ReadAsync(oneByte, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return null;
            }

            var value = oneByte[0];
            bytes.Add(value);
            matched = value == terminator[matched]
                ? matched + 1
                : value == terminator[0] ? 1 : 0;
            if (matched == terminator.Length)
            {
                return bytes.ToArray();
            }
        }

        return null;
    }

    private static Dictionary<string, string> ParseHeaders(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            result[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        return result;
    }

    private static async Task<byte[]?> ReadChunkedBodyAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        using var body = new MemoryStream();
        while (true)
        {
            var sizeLine = await ReadAsciiLineAsync(stream, 32, cancellationToken).ConfigureAwait(false);
            var extensionSeparator = sizeLine?.IndexOf(';') ?? -1;
            var sizeText = extensionSeparator >= 0 ? sizeLine![..extensionSeparator] : sizeLine;
            if (string.IsNullOrWhiteSpace(sizeText)
                || !int.TryParse(
                    sizeText,
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var chunkSize)
                || chunkSize < 0)
            {
                return null;
            }

            if (chunkSize == 0)
            {
                _ = await ReadAsciiLineAsync(stream, MaximumHeaderBytes, cancellationToken)
                    .ConfigureAwait(false);
                return body.ToArray();
            }

            if (body.Length + chunkSize > MaximumBodyBytes)
            {
                return null;
            }

            var chunk = new byte[chunkSize];
            await stream.ReadExactlyAsync(chunk, cancellationToken).ConfigureAwait(false);
            await body.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (await ReadAsciiLineAsync(stream, 2, cancellationToken).ConfigureAwait(false) != string.Empty)
            {
                return null;
            }
        }
    }

    private static async Task<string?> ReadAsciiLineAsync(
        NetworkStream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(Math.Min(maximumBytes, 128));
        var oneByte = new byte[1];
        while (bytes.Count <= maximumBytes)
        {
            if (await stream.ReadAsync(oneByte, cancellationToken).ConfigureAwait(false) == 0)
            {
                return null;
            }

            if (oneByte[0] == (byte)'\n')
            {
                if (bytes.Count > 0 && bytes[^1] == (byte)'\r')
                {
                    bytes.RemoveAt(bytes.Count - 1);
                }
                return Encoding.ASCII.GetString(bytes.ToArray());
            }

            bytes.Add(oneByte[0]);
        }

        return null;
    }

    private async Task RejectAsync(
        NetworkStream stream,
        int statusCode,
        string reason,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _rejectedCount);
        await WriteResponseAsync(stream, statusCode, reason, cancellationToken).ConfigureAwait(false);
    }

    private static Task WriteResponseAsync(
        NetworkStream stream,
        int statusCode,
        string reason,
        CancellationToken cancellationToken)
    {
        var response = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {statusCode} {reason}\r\nContent-Length: 0\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
        return stream.WriteAsync(response, cancellationToken).AsTask();
    }

    private static bool IsFatal(Exception exception)
    {
        return exception is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or AppDomainUnloadedException
            or BadImageFormatException;
    }

    private sealed record ZoteroSelectionPayload(
        [property: JsonPropertyName("text")] string? Text);
}
