using System.Runtime.CompilerServices;

namespace Yita.Translation;

internal sealed class MockTranslationProvider :
    IStreamingTranslationProvider,
    IStreamingExplanationProvider,
    IStreamingQuestionAnswerProvider,
    IStreamingSummaryProvider
{
    public string Id => "mock";

    public async IAsyncEnumerable<TranslationChunk> TranslateAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
        cancellationToken.ThrowIfCancellationRequested();

        var translated = request.Text.Trim() switch
        {
            "Hello world" => "你好，世界",
            "Good morning" => "早上好",
            _ => "这是模拟译文。",
        };

        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        yield return new TranslationChunk(translated, IsFinal: true);
    }

    public async IAsyncEnumerable<TranslationChunk> ExplainAsync(
        ExplanationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SubjectText);
        cancellationToken.ThrowIfCancellationRequested();

        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Scope == ExplanationScope.CodeAnalysis)
        {
            yield return new TranslationChunk(
                $"""
                语言判断：
                这是模拟代码分析，需根据实际语法确认语言。

                常见用途：
                用于演示所选代码片段的典型使用场景。

                代码作用：
                {request.SubjectText.Trim()}

                关键逻辑：
                请结合变量、调用和控制流阅读。

                缩写与命名：
                模拟模式不推断英文全称。真实 AI 分析会逐项说明缩写全称、中文含义、命名原因与使用习惯；无法确定时会明确标注。

                替代与注意：
                实际项目中请确认依赖、输入边界和错误处理。
                """,
                IsFinal: true);
            yield break;
        }

        yield return new TranslationChunk(
            $"释义：{request.SubjectText.Trim()}。{Environment.NewLine}要点：这是模拟 AI 解释。{Environment.NewLine}语境：请结合原文和译文理解。",
            IsFinal: true);
    }

    public async IAsyncEnumerable<TranslationChunk> AnswerAsync(
        QuestionAnswerRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Question);
        cancellationToken.ThrowIfCancellationRequested();

        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        var answer = string.Equals(request.UiLanguage, "zh-CN", StringComparison.OrdinalIgnoreCase)
            ? $"这是对“{request.Question.Trim()}”的模拟回答。"
            : $"This is a mock answer to “{request.Question.Trim()}”.";
        yield return new TranslationChunk(answer, IsFinal: true);
    }

    public async IAsyncEnumerable<TranslationChunk> SummarizeAsync(
        SummaryRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Records);
        cancellationToken.ThrowIfCancellationRequested();

        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        var summary = string.Equals(request.UiLanguage, "zh-CN", StringComparison.OrdinalIgnoreCase)
            ? "## 分类总结\n\n- 模拟记录摘要。\n\n## 关键术语\n\n- Yita"
            : "## Categorized summary\n\n- Mock record summary.\n\n## Key terms\n\n- Yita";
        yield return new TranslationChunk(summary, IsFinal: true);
    }
}
