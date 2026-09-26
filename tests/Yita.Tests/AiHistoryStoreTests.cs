using System.IO;
using Yita.Services;
using Yita.Settings;
using Yita.Translation;

namespace Yita.Tests;

public sealed class AiHistoryStoreTests
{
    [Fact]
    public async Task DisabledHistory_WritesNoFiles()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var store = new AiHistoryStore();
            var result = await store.AppendQuestionAnswerAsync(
                AppSettings.Default with { AiHistoryDirectory = directory, AiHistoryEnabled = false },
                CreateContext(DateTimeOffset.Now),
                "Question",
                "Answer",
                QuestionContextKind.Translation,
                DateTimeOffset.Now);

            Assert.False(result.Saved);
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SameSelection_AppendsExplanationAndAnswersToOneMarkdownFile()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var store = new AiHistoryStore();
            var createdAt = DateTimeOffset.Now;
            var context = CreateContext(createdAt);
            var settings = AppSettings.Default with
            {
                AiHistoryEnabled = true,
                AiHistoryDirectory = directory,
            };

            await store.AppendExplanationAsync(
                settings,
                context,
                "selected ``` phrase",
                ExplanationScope.TranslationSelection,
                "释义内容",
                createdAt.AddSeconds(1));
            await store.AppendQuestionAnswerAsync(
                settings,
                context,
                "Why?",
                "Because.",
                QuestionContextKind.Explanation,
                createdAt.AddSeconds(2));

            var files = Directory.GetFiles(
                Path.Combine(directory, "Yita Records", "Records"),
                "*.md");
            var file = Assert.Single(files);
            var markdown = await File.ReadAllTextAsync(file);
            Assert.Contains($"schema: {AiHistoryStore.RecordSchema}", markdown, StringComparison.Ordinal);
            Assert.Contains("## Original", markdown, StringComparison.Ordinal);
            Assert.Contains("## Explanations", markdown, StringComparison.Ordinal);
            Assert.Contains("## Q&A", markdown, StringComparison.Ordinal);
            Assert.Contains("selected ``` phrase", markdown, StringComparison.Ordinal);
            Assert.Contains("````", markdown, StringComparison.Ordinal);
            Assert.Contains("Context: explanation", markdown, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReadDocuments_FiltersDateRangeAndIgnoresForeignMarkdown()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var store = new AiHistoryStore();
            var now = new DateTimeOffset(2026, 8, 28, 15, 0, 0, TimeSpan.FromHours(8));
            var settings = AppSettings.Default with
            {
                AiHistoryEnabled = true,
                AiHistoryDirectory = directory,
            };
            await store.AppendQuestionAnswerAsync(
                settings,
                CreateContext(now),
                "Today?",
                "Today.",
                QuestionContextKind.Translation,
                now);
            await store.AppendQuestionAnswerAsync(
                settings,
                CreateContext(now.AddDays(-8)),
                "Old?",
                "Old.",
                QuestionContextKind.Translation,
                now.AddDays(-8));
            var recordsDirectory = Path.Combine(directory, "Yita Records", "Records");
            await File.WriteAllTextAsync(Path.Combine(recordsDirectory, "foreign.md"), "# Not an app record");

            var today = await store.ReadDocumentsAsync(directory, SummaryRange.Today, now);
            var sevenDays = await store.ReadDocumentsAsync(directory, SummaryRange.LastSevenDays, now);
            var all = await store.ReadDocumentsAsync(directory, SummaryRange.All, now);

            Assert.Single(today);
            Assert.Single(sevenDays);
            Assert.Equal(2, all.Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task WriteSummary_NeverOverwritesAnExistingSummary()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var store = new AiHistoryStore();
            var generatedAt = DateTimeOffset.Now;

            var first = await store.WriteSummaryAsync(
                directory, SummaryRange.Today, 1, "First", generatedAt);
            var second = await store.WriteSummaryAsync(
                directory, SummaryRange.Today, 1, "Second", generatedAt);

            Assert.NotEqual(first, second);
            Assert.Equal(2, Directory.GetFiles(Path.GetDirectoryName(first)!, "*.md").Length);
            Assert.Contains("First", await File.ReadAllTextAsync(first), StringComparison.Ordinal);
            Assert.Contains("Second", await File.ReadAllTextAsync(second), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ManualRecord_WorksWithAutomaticHistoryOff_AndAppendsToOneDailyFile()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var store = new AiHistoryStore();
            var recordedAt = new DateTimeOffset(new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Local));
            var settings = AppSettings.Default with
            {
                AiHistoryEnabled = false,
                AiHistoryDirectory = directory,
            };
            var explanation = await store.AppendManualRecordAsync(
                settings,
                new ManualExplanationRecordRequest(
                    recordedAt,
                    Guid.NewGuid(),
                    "zh-CN",
                    "Original ``` text",
                    "译文",
                    "English",
                    "Simplified Chinese",
                    "selected subject",
                    ExplanationScope.SourceText,
                    "有价值的解释。"));
            var conversation = await store.AppendManualRecordAsync(
                settings,
                new ManualConversationRecordRequest(
                    recordedAt.AddMinutes(3),
                    Guid.NewGuid(),
                    "zh-CN",
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    null,
                    QuestionContextKind.GeneralChat,
                    [
                        new ConversationTurn("user", "什么是扩散？"),
                        new ConversationTurn("assistant", "扩散是粒子从高浓度区域向低浓度区域的净迁移。"),
                    ]));

            Assert.True(explanation.Saved);
            Assert.True(conversation.Saved);
            var files = Directory.GetFiles(
                Path.Combine(directory, "Yita Records", "Daily Records"),
                "*.md");
            var file = Assert.Single(files);
            var markdown = await File.ReadAllTextAsync(file);
            Assert.Contains($"schema: {AiHistoryStore.ManualRecordSchema}", markdown, StringComparison.Ordinal);
            Assert.Contains("AI explanation", markdown, StringComparison.Ordinal);
            Assert.Contains("DeepSeek chat", markdown, StringComparison.Ordinal);
            Assert.Contains("有价值的解释。", markdown, StringComparison.Ordinal);
            Assert.Contains("什么是扩散？", markdown, StringComparison.Ordinal);
            Assert.Contains("````", markdown, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ManualRecord_UsesOneFilePerLocalDay()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var store = new AiHistoryStore();
            var firstDay = new DateTimeOffset(new DateTime(2026, 8, 28, 23, 59, 0, DateTimeKind.Local));
            var settings = AppSettings.Default with { AiHistoryDirectory = directory };

            await store.AppendManualRecordAsync(settings, CreateManualExplanation(firstDay));
            await store.AppendManualRecordAsync(settings, CreateManualExplanation(firstDay.AddMinutes(2)));

            var files = Directory.GetFiles(
                Path.Combine(directory, "Yita Records", "Daily Records"),
                "*.md");
            Assert.Equal(2, files.Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ManualRecord_DoesNotOverwriteForeignDailyFile()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var recordedAt = DateTimeOffset.Now;
            var dailyDirectory = Path.Combine(directory, "Yita Records", "Daily Records");
            Directory.CreateDirectory(dailyDirectory);
            var file = Path.Combine(dailyDirectory, $"AI-Records-{recordedAt.ToLocalTime():yyyy-MM-dd}.md");
            const string foreignContent = "# My own file";
            await File.WriteAllTextAsync(file, foreignContent);

            var result = await new AiHistoryStore().AppendManualRecordAsync(
                AppSettings.Default with { AiHistoryDirectory = directory },
                CreateManualExplanation(recordedAt));

            Assert.False(result.Saved);
            Assert.Equal("schema_conflict", result.ErrorCode);
            Assert.Equal(foreignContent, await File.ReadAllTextAsync(file));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ManualRecord_WithoutFolder_RequestsDirectory()
    {
        var result = await new AiHistoryStore().AppendManualRecordAsync(
            AppSettings.Default with { AiHistoryEnabled = false, AiHistoryDirectory = string.Empty },
            CreateManualExplanation(DateTimeOffset.Now));

        Assert.False(result.Saved);
        Assert.Equal("directory_required", result.ErrorCode);
    }

    private static AiHistoryContext CreateContext(DateTimeOffset createdAt) => new(
        Guid.NewGuid(),
        createdAt,
        "Original text",
        "译文",
        "English",
        "Simplified Chinese");

    private static ManualExplanationRecordRequest CreateManualExplanation(DateTimeOffset recordedAt) => new(
        recordedAt,
        Guid.NewGuid(),
        "en",
        "Original text",
        "译文",
        "English",
        "Simplified Chinese",
        "Original text",
        ExplanationScope.SourceText,
        "解释内容");

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"Yita-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
