using Yita.Translation;

namespace Yita.Tests;

public sealed class MockTranslationProviderTests
{
    [Theory]
    [InlineData("Hello world", "你好，世界")]
    [InlineData("Good morning", "早上好")]
    [InlineData("Something else", "这是模拟译文。")]
    public async Task TranslateAsync_ReturnsDeterministicResult(string source, string expected)
    {
        var provider = new MockTranslationProvider();
        var chunks = new List<TranslationChunk>();

        await foreach (var chunk in provider.TranslateAsync(
                           new TranslationRequest(source, "自动检测", "简体中文"),
                           CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        var resultChunk = Assert.Single(chunks);
        Assert.Equal(expected, resultChunk.TextDelta);
        Assert.True(resultChunk.IsFinal);
    }

    [Fact]
    public async Task ExplainAsync_ReturnsChineseStructuredResult()
    {
        var provider = new MockTranslationProvider();
        var chunks = new List<TranslationChunk>();

        await foreach (var chunk in provider.ExplainAsync(
                           new ExplanationRequest(
                               "direct",
                               "Make it direct.",
                               "让它直接。",
                               "English",
                               "Simplified Chinese",
                               ExplanationScope.TranslationSelection),
                           CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        var resultChunk = Assert.Single(chunks);
        Assert.Contains("释义：direct", resultChunk.TextDelta, StringComparison.Ordinal);
        Assert.Contains("要点：", resultChunk.TextDelta, StringComparison.Ordinal);
        Assert.True(resultChunk.IsFinal);
    }

    [Fact]
    public async Task ExplainAsync_CodeAnalysis_ReturnsCodeAnalysisStructure()
    {
        var provider = new MockTranslationProvider();
        var chunks = new List<TranslationChunk>();

        await foreach (var chunk in provider.ExplainAsync(
                           new ExplanationRequest(
                               "Console.WriteLine(\"Hello\");",
                               "Console.WriteLine(\"Hello\");",
                               "Console.WriteLine(\"Hello\");",
                               "Auto detect",
                               "Simplified Chinese",
                               ExplanationScope.CodeAnalysis),
                           CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        var resultChunk = Assert.Single(chunks);
        Assert.Contains("语言判断：", resultChunk.TextDelta, StringComparison.Ordinal);
        Assert.Contains("常见用途：", resultChunk.TextDelta, StringComparison.Ordinal);
        Assert.Contains("缩写与命名：", resultChunk.TextDelta, StringComparison.Ordinal);
        Assert.Contains("命名原因与使用习惯", resultChunk.TextDelta, StringComparison.Ordinal);
        Assert.Contains("替代与注意：", resultChunk.TextDelta, StringComparison.Ordinal);
        Assert.True(resultChunk.IsFinal);
    }

    [Fact]
    public async Task AnswerAsync_ReturnsUiLanguageAndFinalChunk()
    {
        var provider = new MockTranslationProvider();
        var chunks = new List<TranslationChunk>();

        await foreach (var chunk in provider.AnswerAsync(
                           new QuestionAnswerRequest(
                               "Why?",
                               "source",
                               "译文",
                               null,
                               "English",
                               "Chinese",
                               "en",
                               QuestionContextKind.Translation,
                               []),
                           CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        Assert.NotEmpty(chunks);
        Assert.True(chunks[^1].IsFinal);
        Assert.Contains("Why?", string.Concat(chunks.Select(chunk => chunk.TextDelta)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SummarizeAsync_ReturnsMarkdownSummary()
    {
        var provider = new MockTranslationProvider();
        var output = new List<TranslationChunk>();

        await foreach (var chunk in provider.SummarizeAsync(
                           new SummaryRequest("record", "zh-CN", "today"),
                           CancellationToken.None))
        {
            output.Add(chunk);
        }

        Assert.True(output[^1].IsFinal);
        Assert.Contains("总结", string.Concat(output.Select(chunk => chunk.TextDelta)), StringComparison.Ordinal);
    }
}
