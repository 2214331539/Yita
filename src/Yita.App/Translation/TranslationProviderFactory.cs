using System.Net;
using System.Net.Http;
using System.Reflection;
using Yita.Settings;

namespace Yita.Translation;

internal sealed class TranslationProviderFactory : ITranslationProviderFactory, IDisposable
{
    private readonly MockTranslationProvider _mockProvider = new();
    private readonly HttpClient _httpClient;
    private readonly ProviderCircuitBreaker _deepSeekCircuitBreaker = new();

    public TranslationProviderFactory()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 4,
            EnableMultipleHttp2Connections = true,
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
        };
        _httpClient = new HttpClient(handler)
        {
            // The coordinator owns cancellation and the user-facing timeout.
            // A second HttpClient timeout used to race that policy and could leave
            // the popup permanently displaying its loading state.
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"Yita/{version?.ToString(3) ?? "0.0.0"}");
    }

    public IStreamingTranslationProvider Create(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return settings.ProviderId.ToLowerInvariant() switch
        {
            "mock" => _mockProvider,
            "deepseek" => CreateDeepSeekProvider(settings),
            _ => throw new TranslationProviderException(
                $"未知翻译 Provider：{settings.ProviderId}",
                TranslationFailureKind.Configuration),
        };
    }

    public IStreamingExplanationProvider CreateExplanationProvider(AppSettings settings)
    {
        var provider = Create(settings);
        return provider as IStreamingExplanationProvider
            ?? throw new TranslationProviderException(
                $"当前翻译 Provider 不支持 AI 解释：{settings.ProviderId}",
                TranslationFailureKind.Configuration);
    }

    public IStreamingQuestionAnswerProvider CreateQuestionAnswerProvider(AppSettings settings)
    {
        var provider = Create(settings);
        return provider as IStreamingQuestionAnswerProvider
            ?? throw new TranslationProviderException(
                $"当前翻译 Provider 不支持 AI 问答：{settings.ProviderId}",
                TranslationFailureKind.Configuration);
    }

    public IStreamingSummaryProvider CreateSummaryProvider(AppSettings settings)
    {
        var provider = Create(settings);
        return provider as IStreamingSummaryProvider
            ?? throw new TranslationProviderException(
                $"当前翻译 Provider 不支持 AI 总结：{settings.ProviderId}",
                TranslationFailureKind.Configuration);
    }

    private IStreamingTranslationProvider CreateDeepSeekProvider(AppSettings settings)
    {
        if (!TryValidateEndpoint(settings.DeepSeekEndpoint, out var endpoint))
        {
            throw new TranslationProviderException(
                "DeepSeek Endpoint 无效；远程地址必须使用 HTTPS。",
                TranslationFailureKind.Configuration);
        }

        if (string.IsNullOrWhiteSpace(settings.DeepSeekModel))
        {
            throw new TranslationProviderException(
                "尚未配置 DeepSeek Model，请从托盘打开设置。",
                TranslationFailureKind.Configuration);
        }

        return new CircuitBreakingTranslationProvider(
            new DeepSeekStreamingProvider(
                _httpClient,
                new OpenAiCompatibleProviderOptions(
                    endpoint!,
                    settings.DeepSeekModel,
                    settings.DeepSeekApiKey)),
            _deepSeekCircuitBreaker);
    }

    internal static bool TryValidateEndpoint(string? value, out Uri? endpoint)
    {
        endpoint = null;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var candidate)
            || candidate.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(candidate.UserInfo)
            || !string.IsNullOrEmpty(candidate.Fragment)
            || !string.IsNullOrEmpty(candidate.Query))
        {
            return false;
        }

        if (candidate.Scheme == Uri.UriSchemeHttp && !candidate.IsLoopback)
        {
            return false;
        }

        endpoint = candidate;
        return true;
    }

    public void Dispose() => _httpClient.Dispose();
}
