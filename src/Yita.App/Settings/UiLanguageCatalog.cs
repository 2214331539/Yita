namespace Yita.Settings;

internal sealed record UiLanguageOption(string Id, string DisplayName);

internal static class UiLanguageCatalog
{
    internal const string EnglishLanguageId = "en";
    internal const string SimplifiedChineseLanguageId = "zh-CN";
    internal const string DefaultLanguageId = EnglishLanguageId;

    internal static IReadOnlyList<UiLanguageOption> Options { get; } =
    [
        new(EnglishLanguageId, "English"),
        new(SimplifiedChineseLanguageId, "简体中文"),
    ];

    internal static string Normalize(string? value)
    {
        var candidate = value?.Trim();
        if (string.IsNullOrEmpty(candidate))
        {
            return DefaultLanguageId;
        }

        var supported = Options.FirstOrDefault(
            option => option.Id.Equals(candidate, StringComparison.OrdinalIgnoreCase));
        if (supported is not null)
        {
            return supported.Id;
        }

        return candidate.ToLowerInvariant() switch
        {
            "english" => EnglishLanguageId,
            "zh" or "zh-cn" or "zh-hans" or "中文" or "简体中文" => SimplifiedChineseLanguageId,
            _ => DefaultLanguageId,
        };
    }

    internal static bool IsSupported(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && Options.Any(option => option.Id.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    internal static string LocalizeProviderError(string? language, string? message)
    {
        var value = message?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return Normalize(language) == SimplifiedChineseLanguageId
                ? "翻译失败，请检查 DeepSeek 设置。"
                : "Translation failed. Check your DeepSeek settings and network.";
        }

        if (Normalize(language) == SimplifiedChineseLanguageId)
        {
            return value;
        }

        if (value.StartsWith("DeepSeek API 返回 ", StringComparison.Ordinal))
        {
            return "DeepSeek API returned " + value["DeepSeek API 返回 ".Length..].Replace("：", ": ", StringComparison.Ordinal);
        }

        if (value.StartsWith("DeepSeek：", StringComparison.Ordinal))
        {
            return "DeepSeek: " + value["DeepSeek：".Length..];
        }

        if (!value.Any(character => character is >= '\u3400' and <= '\u9FFF'))
        {
            return value;
        }

        return value switch
        {
            _ when value.Contains("Endpoint 无效", StringComparison.Ordinal) =>
                "The DeepSeek endpoint is invalid. Remote addresses must use HTTPS.",
            _ when value.Contains("尚未配置 DeepSeek Model", StringComparison.Ordinal) =>
                "The DeepSeek model is not configured. Open Settings from the tray.",
            _ when value.Contains("尚未配置 DeepSeek API Key", StringComparison.Ordinal) =>
                "The DeepSeek API key is not configured. Open Settings from the tray.",
            _ when value.Contains("无法连接 DeepSeek API", StringComparison.Ordinal) =>
                "Could not connect to DeepSeek. Check the network and API endpoint.",
            _ when value.Contains("连接 DeepSeek API 超时", StringComparison.Ordinal) =>
                "The DeepSeek connection timed out. Check the network or VPN.",
            _ when value.Contains("连续网络故障暂时暂停", StringComparison.Ordinal) =>
                "DeepSeek is temporarily paused after repeated network failures. Wait a few seconds and try again.",
            _ when value.Contains("非流式响应", StringComparison.Ordinal) =>
                "DeepSeek returned a non-streaming response. Check the endpoint and model.",
            _ when value.Contains("未返回译文", StringComparison.Ordinal)
                || value.Contains("未收到译文", StringComparison.Ordinal)
                || value.Contains("未返回可用的译文", StringComparison.Ordinal) =>
                "DeepSeek returned no translated text.",
            _ when value.Contains("流式连接中断", StringComparison.Ordinal)
                || value.Contains("无法读取 DeepSeek 流式响应", StringComparison.Ordinal) =>
                "The DeepSeek stream was interrupted. Check the network or VPN and try again.",
            _ when value.Contains("无法解析的流式数据", StringComparison.Ordinal) =>
                "DeepSeek returned invalid streaming data.",
            _ when value.Contains("超过 60 秒", StringComparison.Ordinal) =>
                "The DeepSeek request exceeded 60 seconds and was canceled.",
            _ when value.Contains("未返回首段译文", StringComparison.Ordinal) =>
                "DeepSeek did not return translated text within 20 seconds. Check the network and try again.",
            _ when value.Contains("流式响应停顿", StringComparison.Ordinal) =>
                "The DeepSeek stream stopped responding for 15 seconds and was canceled.",
            _ when value.Contains("意外中断", StringComparison.Ordinal) =>
                "The translation ended unexpectedly. Try again.",
            _ when value.Contains("未知翻译 Provider", StringComparison.Ordinal) =>
                "The configured translation provider is not supported.",
            _ => "Translation failed. Check your DeepSeek settings and network.",
        };
    }
}
