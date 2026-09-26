using System.IO;
using Yita.Services;
using Yita.Settings;
using Yita.Translation;

namespace Yita.Tests;

public sealed class AiSummaryServiceTests
{
    [Fact]
    public void CreateBatches_RespectsLimitAndPreservesContent()
    {
        var documents = new[]
        {
            new string('a', 20_000),
            new string('b', 20_000),
            new string('c', 40_000),
        };

        var batches = AiSummaryService.CreateBatches(documents);

        Assert.True(batches.Count >= 3);
        Assert.All(batches, batch => Assert.InRange(batch.Length, 1, 28_000));
        var joined = string.Concat(batches).Replace(Environment.NewLine + "--- RECORD ---" + Environment.NewLine, string.Empty, StringComparison.Ordinal);
        Assert.Equal(string.Concat(documents), joined);
    }

    [Fact]
    public async Task GenerateAsync_WithNoRecords_DoesNotCallProvider()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var factory = new RecordingFactory();
            var service = new AiSummaryService(new AiHistoryStore(), factory);

            var result = await service.GenerateAsync(
                AppSettings.Default with
                {
                    AiHistoryEnabled = true,
                    AiHistoryDirectory = directory,
                },
                SummaryRange.Today);

            Assert.False(result.Succeeded);
            Assert.Equal("no_records", result.ErrorCode);
            Assert.Equal(0, factory.SummaryProviderCreateCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task GenerateAsync_WritesASeparateSchemaMarkedSummary()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var store = new AiHistoryStore();
            var now = DateTimeOffset.Now;
            var settings = AppSettings.Default with
            {
                AiHistoryEnabled = true,
                AiHistoryDirectory = directory,
                ProviderId = "mock",
            };
            await store.AppendQuestionAnswerAsync(
                settings,
                new AiHistoryContext(Guid.NewGuid(), now, "Hello", "你好", "English", "Chinese"),
                "Usage?",
                "Greeting.",
                QuestionContextKind.Translation,
                now);
            var service = new AiSummaryService(store, new RecordingFactory());

            var result = await service.GenerateAsync(settings, SummaryRange.Today);

            Assert.True(result.Succeeded);
            Assert.Equal(1, result.RecordCount);
            Assert.True(File.Exists(result.OutputPath));
            var markdown = await File.ReadAllTextAsync(result.OutputPath!);
            Assert.Contains($"schema: {AiHistoryStore.SummarySchema}", markdown, StringComparison.Ordinal);
            Assert.Contains("Categorized summary", markdown, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"Yita-summary-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class RecordingFactory : ITranslationProviderFactory
    {
        public int SummaryProviderCreateCount { get; private set; }

        public IStreamingTranslationProvider Create(AppSettings settings) => new MockTranslationProvider();

        public IStreamingExplanationProvider CreateExplanationProvider(AppSettings settings) => new MockTranslationProvider();

        public IStreamingQuestionAnswerProvider CreateQuestionAnswerProvider(AppSettings settings) => new MockTranslationProvider();

        public IStreamingSummaryProvider CreateSummaryProvider(AppSettings settings)
        {
            SummaryProviderCreateCount++;
            return new MockTranslationProvider();
        }
    }
}
