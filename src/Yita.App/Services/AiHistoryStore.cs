using System.Globalization;
using System.IO;
using System.Text;
using Yita.Settings;
using Yita.Translation;

namespace Yita.Services;

internal sealed record AiHistoryContext(
    Guid SessionId,
    DateTimeOffset CreatedAt,
    string SourceText,
    string TranslationText,
    string SourceLanguage,
    string TargetLanguage);

internal sealed record AiHistoryWriteResult(bool Saved, string? ErrorCode = null)
{
    internal static AiHistoryWriteResult Skipped { get; } = new(false);
}

internal sealed record AiHistoryDocument(DateTimeOffset CreatedAt, string Markdown);

internal abstract record ManualAiRecordRequest(
    DateTimeOffset RecordedAt,
    Guid SessionId,
    string UiLanguage,
    string SourceText,
    string TranslationText,
    string SourceLanguage,
    string TargetLanguage);

internal sealed record ManualExplanationRecordRequest(
    DateTimeOffset RecordedAt,
    Guid SessionId,
    string UiLanguage,
    string SourceText,
    string TranslationText,
    string SourceLanguage,
    string TargetLanguage,
    string SubjectText,
    ExplanationScope Scope,
    string Explanation)
    : ManualAiRecordRequest(
        RecordedAt,
        SessionId,
        UiLanguage,
        SourceText,
        TranslationText,
        SourceLanguage,
        TargetLanguage);

internal sealed record ManualConversationRecordRequest(
    DateTimeOffset RecordedAt,
    Guid SessionId,
    string UiLanguage,
    string SourceText,
    string TranslationText,
    string SourceLanguage,
    string TargetLanguage,
    string? ExplanationText,
    QuestionContextKind ContextKind,
    IReadOnlyList<ConversationTurn> Turns)
    : ManualAiRecordRequest(
        RecordedAt,
        SessionId,
        UiLanguage,
        SourceText,
        TranslationText,
        SourceLanguage,
        TargetLanguage);

internal sealed class AiHistoryStore
{
    internal const string RecordSchema = "yita-history/v1";
    internal const string SummarySchema = "yita-summary/v1";
    internal const string ManualRecordSchema = "yita-manual-record/v1";
    private const string RootDirectoryName = "Yita Records";
    private const string RecordsDirectoryName = "Records";
    private const string SummariesDirectoryName = "Summaries";
    private const string DailyRecordsDirectoryName = "Daily Records";
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Dictionary<string, SessionState> _sessions = new(StringComparer.OrdinalIgnoreCase);

    internal async Task<AiHistoryWriteResult> AppendExplanationAsync(
        AppSettings settings,
        AiHistoryContext context,
        string subjectText,
        ExplanationScope scope,
        string explanation,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        if (!settings.AiHistoryEnabled
            || string.IsNullOrWhiteSpace(subjectText)
            || string.IsNullOrWhiteSpace(explanation))
        {
            return AiHistoryWriteResult.Skipped;
        }

        return await UpdateSessionAsync(
            settings,
            context,
            state => state.Explanations.Add(new ExplanationEntry(
                completedAt,
                subjectText,
                scope,
                explanation)),
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<AiHistoryWriteResult> AppendQuestionAnswerAsync(
        AppSettings settings,
        AiHistoryContext context,
        string question,
        string answer,
        QuestionContextKind contextKind,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        if (!settings.AiHistoryEnabled
            || string.IsNullOrWhiteSpace(question)
            || string.IsNullOrWhiteSpace(answer))
        {
            return AiHistoryWriteResult.Skipped;
        }

        return await UpdateSessionAsync(
            settings,
            context,
            state => state.QuestionAnswers.Add(new QuestionAnswerEntry(
                completedAt,
                question,
                answer,
                contextKind)),
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<AiHistoryWriteResult> AppendManualRecordAsync(
        AppSettings settings,
        ManualAiRecordRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!HasManualRecordContent(request))
        {
            return new AiHistoryWriteResult(false, "no_content");
        }

        if (string.IsNullOrWhiteSpace(settings.AiHistoryDirectory))
        {
            return new AiHistoryWriteResult(false, "directory_required");
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            HistoryPaths paths;
            try
            {
                paths = ResolvePaths(settings.AiHistoryDirectory, create: true);
            }
            catch (Exception exception) when (exception is ArgumentException
                                              or NotSupportedException
                                              or IOException
                                              or UnauthorizedAccessException)
            {
                return new AiHistoryWriteResult(false, "directory_unavailable");
            }

            var localRecordedAt = request.RecordedAt.ToLocalTime();
            var destination = Path.Combine(
                paths.DailyRecordsDirectory,
                $"AI-Records-{localRecordedAt:yyyy-MM-dd}.md");
            string existing = string.Empty;
            if (File.Exists(destination))
            {
                try
                {
                    existing = await File.ReadAllTextAsync(destination, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return new AiHistoryWriteResult(false, "write_failed");
                }

                if (!HasSchema(existing, ManualRecordSchema))
                {
                    return new AiHistoryWriteResult(false, "schema_conflict");
                }
            }

            var markdown = string.IsNullOrEmpty(existing)
                ? RenderManualRecordDocumentHeader(localRecordedAt, request.RecordedAt)
                : existing.TrimEnd() + Environment.NewLine + Environment.NewLine;
            markdown += RenderManualRecordEntry(request);
            try
            {
                await WriteAtomicallyAsync(destination, markdown, cancellationToken)
                    .ConfigureAwait(false);
                return new AiHistoryWriteResult(true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new AiHistoryWriteResult(false, "write_failed");
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    internal async Task<IReadOnlyList<AiHistoryDocument>> ReadDocumentsAsync(
        string baseDirectory,
        SummaryRange range,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var paths = ResolvePaths(baseDirectory, create: false);
        if (!Directory.Exists(paths.RecordsDirectory))
        {
            return [];
        }

        var lowerBound = GetLowerBound(range, now);
        var documents = new List<AiHistoryDocument>();
        foreach (var file in Directory.EnumerateFiles(paths.RecordsDirectory, "*.md", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string markdown;
            try
            {
                markdown = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            if (!HasSchema(markdown, RecordSchema)
                || !TryReadCreatedAt(markdown, out var createdAt)
                || lowerBound is { } minimum && createdAt.ToLocalTime() < minimum)
            {
                continue;
            }

            documents.Add(new AiHistoryDocument(createdAt, markdown));
        }

        return documents
            .OrderBy(document => document.CreatedAt)
            .ToArray();
    }

    internal async Task<string> WriteSummaryAsync(
        string baseDirectory,
        SummaryRange range,
        int recordCount,
        string summaryMarkdown,
        DateTimeOffset generatedAt,
        CancellationToken cancellationToken = default)
    {
        var paths = ResolvePaths(baseDirectory, create: true);
        var stem = $"AI-Summary-{generatedAt.ToLocalTime():yyyy-MM-dd_HHmmssfff}";
        var destination = Path.Combine(paths.SummariesDirectory, stem + ".md");
        for (var suffix = 2; File.Exists(destination); suffix++)
        {
            destination = Path.Combine(paths.SummariesDirectory, $"{stem}-{suffix}.md");
        }
        var content = $"""
            ---
            schema: {SummarySchema}
            generated_at: {generatedAt:O}
            range: {SummaryRangeCatalog.ToStableId(range)}
            record_count: {recordCount.ToString(CultureInfo.InvariantCulture)}
            ---

            # AI Summary · {generatedAt.ToLocalTime():yyyy-MM-dd HH:mm}

            {summaryMarkdown.Trim()}
            """;
        await WriteAtomicallyAsync(destination, content + Environment.NewLine, cancellationToken)
            .ConfigureAwait(false);
        return destination;
    }

    internal static bool TryValidateWritableDirectory(string? baseDirectory, out string? errorCode)
    {
        errorCode = null;
        try
        {
            var paths = ResolvePaths(baseDirectory, create: true);
            var probe = Path.Combine(paths.RootDirectory, $".write-test-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "Yita write test", new UTF8Encoding(false));
            File.Delete(probe);
            return true;
        }
        catch (ArgumentException)
        {
            errorCode = "invalid_path";
        }
        catch (NotSupportedException)
        {
            errorCode = "invalid_path";
        }
        catch (IOException)
        {
            errorCode = "io_error";
        }
        catch (UnauthorizedAccessException)
        {
            errorCode = "access_denied";
        }

        return false;
    }

    private async Task<AiHistoryWriteResult> UpdateSessionAsync(
        AppSettings settings,
        AiHistoryContext context,
        Action<SessionState> update,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            HistoryPaths paths;
            try
            {
                paths = ResolvePaths(settings.AiHistoryDirectory, create: true);
            }
            catch (Exception exception) when (exception is ArgumentException
                                              or NotSupportedException
                                              or IOException
                                              or UnauthorizedAccessException)
            {
                return new AiHistoryWriteResult(false, "directory_unavailable");
            }

            var key = $"{paths.RootDirectory}|{context.SessionId:N}";
            if (!_sessions.TryGetValue(key, out var state))
            {
                var suffix = context.SessionId.ToString("N")[..8];
                var fileName = $"{context.CreatedAt.ToLocalTime():yyyy-MM-dd_HHmmss}_{suffix}.md";
                state = new SessionState(
                    Path.Combine(paths.RecordsDirectory, fileName),
                    context);
                _sessions.Add(key, state);
            }

            state.Context = context;
            update(state);
            var markdown = RenderSession(state);
            try
            {
                await WriteAtomicallyAsync(state.FilePath, markdown, cancellationToken)
                    .ConfigureAwait(false);
                return new AiHistoryWriteResult(true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new AiHistoryWriteResult(false, "write_failed");
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static string RenderSession(SessionState state)
    {
        var context = state.Context;
        var builder = new StringBuilder();
        builder.AppendLine("---");
        builder.Append("schema: ").AppendLine(RecordSchema);
        builder.Append("session_id: ").AppendLine(context.SessionId.ToString("N"));
        builder.Append("created_at: ").AppendLine(context.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        builder.Append("source_language: ").AppendLine(EscapeYaml(context.SourceLanguage));
        builder.Append("target_language: ").AppendLine(EscapeYaml(context.TargetLanguage));
        builder.AppendLine("---");
        builder.AppendLine();
        builder.Append("# Yita Record · ")
            .AppendLine(context.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture));
        builder.AppendLine();
        AppendFencedSection(builder, "Original", context.SourceText);
        AppendFencedSection(builder, "Translation", context.TranslationText);

        if (state.Explanations.Count > 0)
        {
            builder.AppendLine("## Explanations");
            builder.AppendLine();
            for (var index = 0; index < state.Explanations.Count; index++)
            {
                var entry = state.Explanations[index];
                builder.Append("### ").Append(index + 1).Append(" · ")
                    .AppendLine(entry.CompletedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture));
                builder.Append("Scope: ").AppendLine(DescribeExplanationScope(entry.Scope));
                builder.AppendLine();
                AppendFencedSection(builder, "Subject", entry.SubjectText, headingLevel: 4);
                builder.AppendLine("#### AI explanation");
                builder.AppendLine();
                builder.AppendLine(entry.Explanation.Trim());
                builder.AppendLine();
            }
        }

        if (state.QuestionAnswers.Count > 0)
        {
            builder.AppendLine("## Q&A");
            builder.AppendLine();
            for (var index = 0; index < state.QuestionAnswers.Count; index++)
            {
                var entry = state.QuestionAnswers[index];
                builder.Append("### ").Append(index + 1).Append(" · ")
                    .AppendLine(entry.CompletedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture));
                builder.Append("Context: ").AppendLine(entry.ContextKind == QuestionContextKind.Explanation
                    ? "explanation"
                    : "translation");
                builder.AppendLine();
                AppendFencedSection(builder, "Question", entry.Question, headingLevel: 4);
                builder.AppendLine("#### Answer");
                builder.AppendLine();
                builder.AppendLine(entry.Answer.Trim());
                builder.AppendLine();
            }
        }

        return builder.ToString();
    }

    private static bool HasManualRecordContent(ManualAiRecordRequest request) => request switch
    {
        ManualExplanationRecordRequest explanation =>
            !string.IsNullOrWhiteSpace(explanation.Explanation)
            && !string.IsNullOrWhiteSpace(explanation.SubjectText),
        ManualConversationRecordRequest conversation =>
            conversation.Turns.Count > 0
            && conversation.Turns.Any(turn => !string.IsNullOrWhiteSpace(turn.Content)),
        _ => false,
    };

    private static string RenderManualRecordDocumentHeader(
        DateTimeOffset localRecordedAt,
        DateTimeOffset recordedAt)
    {
        return $"""
            ---
            schema: {ManualRecordSchema}
            date: {localRecordedAt:yyyy-MM-dd}
            created_at: {recordedAt:O}
            ---

            # Yita Daily Records · {localRecordedAt:yyyy-MM-dd}

            """;
    }

    private static string RenderManualRecordEntry(ManualAiRecordRequest request)
    {
        var builder = new StringBuilder();
        var localTime = request.RecordedAt.ToLocalTime();
        var type = request switch
        {
            ManualExplanationRecordRequest { Scope: ExplanationScope.CodeAnalysis } => "Code analysis",
            ManualExplanationRecordRequest => "AI explanation",
            ManualConversationRecordRequest { ContextKind: QuestionContextKind.GeneralChat } => "DeepSeek chat",
            ManualConversationRecordRequest => "AI Q&A",
            _ => "AI record",
        };
        builder.Append("## ")
            .Append(localTime.ToString("HH:mm:ss", CultureInfo.CurrentCulture))
            .Append(" · ")
            .AppendLine(type);
        builder.AppendLine();
        builder.Append("- Recorded at: ").AppendLine(request.RecordedAt.ToString("O", CultureInfo.InvariantCulture));
        builder.Append("- Session: ").AppendLine(request.SessionId.ToString("N"));
        if (!string.IsNullOrWhiteSpace(request.SourceLanguage)
            || !string.IsNullOrWhiteSpace(request.TargetLanguage))
        {
            builder.Append("- Language: ")
                .Append(string.IsNullOrWhiteSpace(request.SourceLanguage) ? "unknown" : request.SourceLanguage.Trim())
                .Append(" → ")
                .AppendLine(string.IsNullOrWhiteSpace(request.TargetLanguage) ? "unknown" : request.TargetLanguage.Trim());
        }

        builder.AppendLine();
        if (!string.IsNullOrWhiteSpace(request.SourceText))
        {
            AppendFencedSection(builder, "Original", request.SourceText, headingLevel: 3);
        }

        if (!string.IsNullOrWhiteSpace(request.TranslationText))
        {
            AppendFencedSection(builder, "Translation", request.TranslationText, headingLevel: 3);
        }

        switch (request)
        {
            case ManualExplanationRecordRequest explanation:
                builder.Append("- Explanation scope: ").AppendLine(
                    DescribeExplanationScope(explanation.Scope));
                builder.AppendLine();
                AppendFencedSection(builder, "Subject", explanation.SubjectText, headingLevel: 3);
                builder.AppendLine("### Explanation");
                builder.AppendLine();
                builder.AppendLine(explanation.Explanation.Trim());
                builder.AppendLine();
                break;

            case ManualConversationRecordRequest conversation:
                builder.Append("- Context: ").AppendLine(conversation.ContextKind switch
                {
                    QuestionContextKind.Explanation => "explanation",
                    QuestionContextKind.GeneralChat => "general chat",
                    _ => "translation",
                });
                builder.AppendLine();
                if (!string.IsNullOrWhiteSpace(conversation.ExplanationText))
                {
                    builder.AppendLine("### Explanation context");
                    builder.AppendLine();
                    builder.AppendLine(conversation.ExplanationText.Trim());
                    builder.AppendLine();
                }

                builder.AppendLine("### Conversation");
                builder.AppendLine();
                for (var index = 0; index < conversation.Turns.Count; index++)
                {
                    var turn = conversation.Turns[index];
                    if (string.IsNullOrWhiteSpace(turn.Content))
                    {
                        continue;
                    }

                    var role = string.Equals(turn.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                        ? "Assistant"
                        : string.Equals(turn.Role, "user", StringComparison.OrdinalIgnoreCase)
                            ? "User"
                            : "Turn";
                    AppendFencedSection(builder, $"{role} {index + 1}", turn.Content, headingLevel: 4);
                }

                break;
        }

        builder.AppendLine("---");
        return builder.ToString();
    }

    private static void AppendFencedSection(
        StringBuilder builder,
        string title,
        string content,
        int headingLevel = 2)
    {
        builder.Append(new string('#', headingLevel)).Append(' ').AppendLine(title);
        builder.AppendLine();
        var fence = CreateFence(content);
        builder.AppendLine(fence);
        builder.AppendLine(content.Trim());
        builder.AppendLine(fence);
        builder.AppendLine();
    }

    private static string CreateFence(string content)
    {
        var maximumRun = 0;
        var currentRun = 0;
        foreach (var character in content)
        {
            if (character == '`')
            {
                currentRun++;
                maximumRun = Math.Max(maximumRun, currentRun);
            }
            else
            {
                currentRun = 0;
            }
        }

        return new string('`', Math.Max(3, maximumRun + 1));
    }

    private static string DescribeExplanationScope(ExplanationScope scope) => scope switch
    {
        ExplanationScope.TranslationSelection => "translation selection",
        ExplanationScope.CodeAnalysis => "code analysis",
        _ => "source text",
    };

    private static string EscapeYaml(string value) =>
        $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal)}\"";

    private static DateTimeOffset? GetLowerBound(SummaryRange range, DateTimeOffset now)
    {
        var localNow = now.ToLocalTime();
        return SummaryRangeCatalog.Normalize(range) switch
        {
            SummaryRange.Today => new DateTimeOffset(localNow.Date, localNow.Offset),
            SummaryRange.LastSevenDays => new DateTimeOffset(localNow.Date.AddDays(-6), localNow.Offset),
            _ => null,
        };
    }

    private static bool HasSchema(string markdown, string schema) =>
        markdown.AsSpan(0, Math.Min(markdown.Length, 512))
            .Contains($"schema: {schema}".AsSpan(), StringComparison.Ordinal);

    private static bool TryReadCreatedAt(string markdown, out DateTimeOffset createdAt)
    {
        createdAt = default;
        using var reader = new StringReader(markdown);
        for (var lineNumber = 0; lineNumber < 16 && reader.ReadLine() is { } line; lineNumber++)
        {
            if (!line.StartsWith("created_at:", StringComparison.Ordinal))
            {
                continue;
            }

            return DateTimeOffset.TryParse(
                line["created_at:".Length..].Trim(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out createdAt);
        }

        return false;
    }

    private static HistoryPaths ResolvePaths(string? baseDirectory, bool create)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory)
            || !Path.IsPathFullyQualified(baseDirectory))
        {
            throw new ArgumentException("AI history directory must be an absolute path.", nameof(baseDirectory));
        }

        var normalizedBase = Path.GetFullPath(baseDirectory.Trim());
        var root = Path.Combine(normalizedBase, RootDirectoryName);
        var records = Path.Combine(root, RecordsDirectoryName);
        var summaries = Path.Combine(root, SummariesDirectoryName);
        var dailyRecords = Path.Combine(root, DailyRecordsDirectoryName);
        if (create)
        {
            Directory.CreateDirectory(records);
            Directory.CreateDirectory(summaries);
            Directory.CreateDirectory(dailyRecords);
        }

        return new HistoryPaths(root, records, summaries, dailyRecords);
    }

    private static async Task WriteAtomicallyAsync(
        string destination,
        string content,
        CancellationToken cancellationToken)
    {
        var temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                    temporary,
                    content,
                    new UTF8Encoding(false),
                    cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class SessionState(string filePath, AiHistoryContext context)
    {
        internal string FilePath { get; } = filePath;

        internal AiHistoryContext Context { get; set; } = context;

        internal List<ExplanationEntry> Explanations { get; } = [];

        internal List<QuestionAnswerEntry> QuestionAnswers { get; } = [];
    }

    private sealed record ExplanationEntry(
        DateTimeOffset CompletedAt,
        string SubjectText,
        ExplanationScope Scope,
        string Explanation);

    private sealed record QuestionAnswerEntry(
        DateTimeOffset CompletedAt,
        string Question,
        string Answer,
        QuestionContextKind ContextKind);

    private sealed record HistoryPaths(
        string RootDirectory,
        string RecordsDirectory,
        string SummariesDirectory,
        string DailyRecordsDirectory);
}
