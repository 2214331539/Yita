using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Yita.Core.Translation;

public sealed class DeepSeekStreamingTranslator : IStreamingTranslator
{
    private readonly HttpClient _httpClient;
    public TranslationProviderOptions Options { get; }

    public DeepSeekStreamingTranslator(HttpClient httpClient, TranslationProviderOptions options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        if (!Options.Endpoint.IsAbsoluteUri || Options.Endpoint.Scheme is not ("http" or "https"))
            throw new ArgumentException("Endpoint 必须使用 http 或 https。", nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(Options.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(Options.ApiKey);
    }

    public async IAsyncEnumerable<TranslationChunk> TranslateAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
        cancellationToken.ThrowIfCancellationRequested();
        // ResponseHeadersRead's HttpClient timeout does not cover the SSE body.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        using var message = new HttpRequestMessage(HttpMethod.Post, BuildUri(Options.Endpoint));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Options.ApiKey);
        message.Content = JsonContent.Create(new
        {
            model = Options.Model,
            stream = true,
            temperature = 0.2,
            messages = new[]
            {
                new { role = "system", content = BuildPrompt(request) },
                new { role = "user", content = request.Text },
            },
        });

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead,
                deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TranslationProviderException("翻译请求超时，请重试。", TranslationFailureKind.Timeout);
        }
        catch (HttpRequestException exception)
        {
            throw new TranslationProviderException("无法连接翻译服务。", exception, TranslationFailureKind.Connectivity);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new TranslationProviderException($"翻译服务返回 HTTP {(int)response.StatusCode}。",
                    response.StatusCode switch
                    {
                        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => TranslationFailureKind.Authentication,
                        HttpStatusCode.TooManyRequests => TranslationFailureKind.RateLimit,
                        HttpStatusCode.BadRequest or HttpStatusCode.NotFound => TranslationFailureKind.InvalidRequest,
                        HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => TranslationFailureKind.Timeout,
                        _ => TranslationFailureKind.Server,
                    });

            await using var chunks = ReadChunksAsync(response.Content, deadline.Token).GetAsyncEnumerator(deadline.Token);
            while (true)
            {
                bool hasNext;
                try { hasNext = await chunks.MoveNextAsync().ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TranslationProviderException("翻译请求超时，请重试。", TranslationFailureKind.Timeout);
                }
                catch (Exception exception) when (exception is IOException or HttpRequestException)
                {
                    throw new TranslationProviderException("翻译连接中断，请重试。", exception, TranslationFailureKind.Connectivity);
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (!hasNext) yield break;
                yield return chunks.Current;
            }
        }
    }

    private static async IAsyncEnumerable<TranslationChunk> ReadChunksAsync(
        HttpContent content, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var reader = new BoundedSseLineReader(stream, 256 * 1024);
        var receivedContent = false;
        var stopped = false;
        var receivedCharacters = 0;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data.Length == 0) continue;
            if (data == "[DONE]")
            {
                if (!receivedContent)
                    throw new TranslationProviderException("翻译服务未返回译文。", TranslationFailureKind.Protocol);
                yield return new TranslationChunk(string.Empty, true);
                yield break;
            }

            string? delta = null;
            try
            {
                using var json = JsonDocument.Parse(data);
                if (json.RootElement.TryGetProperty("error", out _))
                    throw new TranslationProviderException("翻译服务返回了流式错误。", TranslationFailureKind.Server);
                if (!json.RootElement.TryGetProperty("choices", out var choices)
                    || choices.ValueKind != JsonValueKind.Array)
                    throw new JsonException("Missing choices array.");
                if (choices.GetArrayLength() == 0) continue; // Usage-only event.
                var choice = choices[0];
                if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String)
                {
                    if (reason.GetString() != "stop")
                        throw new TranslationProviderException("译文未完整生成，请重试。", TranslationFailureKind.Protocol);
                    stopped = true;
                }
                if (choice.TryGetProperty("delta", out var change)
                    && change.TryGetProperty("content", out var value) && value.ValueKind == JsonValueKind.String)
                    delta = value.GetString();
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                throw new TranslationProviderException("流式响应格式无效。", exception, TranslationFailureKind.Protocol);
            }

            if (string.IsNullOrEmpty(delta)) continue;
            receivedCharacters += delta.Length;
            if (receivedCharacters > 128_000)
                throw new TranslationProviderException("译文过长，已停止读取。", TranslationFailureKind.Protocol);
            receivedContent = true;
            yield return new TranslationChunk(delta);
        }
        if (!receivedContent || !stopped)
            throw new TranslationProviderException("翻译响应提前结束，请重试。", TranslationFailureKind.Protocol);
        yield return new TranslationChunk(string.Empty, true);
    }

    private static string BuildPrompt(TranslationRequest request)
    {
        var prompt = new StringBuilder("Translate faithfully into ").Append(request.TargetLanguage)
            .Append(". Source language: ").Append(request.SourceLanguage)
            .Append(". Return only the translation, preserving paragraph breaks.");
        prompt.Append(request.Mode == "literal" ? " Use a literal translation." : " Use natural, fluent wording.");
        if (request.Tone == "formal") prompt.Append(" Use a formal tone.");
        if (request.Tone == "casual") prompt.Append(" Use a casual tone.");
        if (!string.IsNullOrWhiteSpace(request.Context)) prompt.Append("\nContext:\n").Append(request.Context);
        if (!string.IsNullOrWhiteSpace(request.PersonalGlossary))
            prompt.Append("\nPreferred glossary:\n").Append(request.PersonalGlossary);
        return prompt.ToString();
    }

    private static Uri BuildUri(Uri endpoint)
    {
        var builder = new UriBuilder(endpoint);
        var path = builder.Path.TrimEnd('/');
        if (!path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            path += path.Length == 0 ? "/v1/chat/completions" : "/chat/completions";
        builder.Path = path;
        return builder.Uri;
    }
}
