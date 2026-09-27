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
        if (Options.Endpoint.Scheme is not ("http" or "https"))
            throw new ArgumentException("DeepSeek Endpoint 必须使用 http 或 https。", nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(Options.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(Options.ApiKey);
    }

    public async IAsyncEnumerable<TranslationChunk> TranslateAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
        using var message = new HttpRequestMessage(HttpMethod.Post, BuildUri(Options.Endpoint));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Options.ApiKey);
        message.Content = JsonContent.Create(new
        {
            model = Options.Model,
            stream = true,
            temperature = 0.2,
            messages = new[]
            {
                new { role = "system", content = "Translate the selected text faithfully. Return only the translation." },
                new { role = "user", content = request.Text },
            },
        });

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new TranslationProviderException("翻译请求已取消。", TranslationFailureKind.Cancelled);
        }
        catch (HttpRequestException exception)
        {
            throw new TranslationProviderException("无法连接 DeepSeek API。", exception, TranslationFailureKind.Connectivity);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new TranslationProviderException(
                    $"DeepSeek API 返回 HTTP {(int)response.StatusCode}。",
                    response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                        ? TranslationFailureKind.Authentication
                        : TranslationFailureKind.Server);

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    continue;
                var data = line[5..].Trim();
                if (data is "[DONE]" or "")
                    continue;
                string? delta;
                try
                {
                    using var json = JsonDocument.Parse(data);
                    delta = json.RootElement.GetProperty("choices")[0]
                        .GetProperty("delta").GetProperty("content").GetString();
                }
                catch (Exception exception) when (exception is JsonException or KeyNotFoundException)
                {
                    throw new TranslationProviderException("DeepSeek 流式响应格式无效。", exception, TranslationFailureKind.Protocol);
                }
                if (!string.IsNullOrEmpty(delta))
                    yield return new TranslationChunk(delta);
            }
        }
        yield return new TranslationChunk(string.Empty, true);
    }

    private static Uri BuildUri(Uri endpoint)
    {
        var value = endpoint.ToString().TrimEnd('/');
        return value.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? endpoint
            : new Uri(value + "/v1/chat/completions");
    }
}
