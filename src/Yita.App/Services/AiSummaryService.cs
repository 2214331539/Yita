using System.Text;
using System.IO;
using Yita.Settings;
using Yita.Translation;

namespace Yita.Services;

internal sealed record AiSummaryGenerationResult(
    bool Succeeded,
    int RecordCount,
    string? OutputPath = null,
    string? ErrorCode = null);

internal sealed class AiSummaryService(
    AiHistoryStore historyStore,
    ITranslationProviderFactory providerFactory)
{
    private const int MaximumBatchCharacters = 28_000;
    private static readonly TimeSpan SummaryTimeout = TimeSpan.FromMinutes(3);
    private readonly SemaphoreSlim _summaryConcurrency = new(1, 1);

    internal async Task<AiSummaryGenerationResult> GenerateAsync(
        AppSettings settings,
        SummaryRange range,
        CancellationToken cancellationToken = default)
    {
        if (!settings.AiHistoryEnabled || string.IsNullOrWhiteSpace(settings.AiHistoryDirectory))
        {
            return new AiSummaryGenerationResult(false, 0, ErrorCode: "history_disabled");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SummaryTimeout);
        await _summaryConcurrency.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            var documents = await historyStore.ReadDocumentsAsync(
                    settings.AiHistoryDirectory,
                    range,
                    DateTimeOffset.Now,
                    timeout.Token)
                .ConfigureAwait(false);
            if (documents.Count == 0)
            {
                return new AiSummaryGenerationResult(false, 0, ErrorCode: "no_records");
            }

            var provider = providerFactory.CreateSummaryProvider(settings);
            var rangeLabel = SummaryRangeCatalog.ToStableId(range);
            var batches = CreateBatches(documents.Select(document => document.Markdown));
            var partials = new List<string>(batches.Count);
            foreach (var batch in batches)
            {
                partials.Add(await CollectAsync(
                        provider.SummarizeAsync(
                            new SummaryRequest(batch, settings.UiLanguage, rangeLabel),
                            timeout.Token),
                        timeout.Token)
                    .ConfigureAwait(false));
            }

            while (partials.Count > 1)
            {
                var next = new List<string>();
                foreach (var batch in CreateBatches(partials))
                {
                    next.Add(await CollectAsync(
                            provider.SummarizeAsync(
                                new SummaryRequest(
                                    batch,
                                    settings.UiLanguage,
                                    rangeLabel,
                                    IsConsolidation: true),
                                timeout.Token),
                            timeout.Token)
                        .ConfigureAwait(false));
                }

                if (next.Count >= partials.Count)
                {
                    var joined = string.Join(Environment.NewLine + Environment.NewLine, next);
                    partials =
                    [
                        await CollectAsync(
                                provider.SummarizeAsync(
                                    new SummaryRequest(
                                        joined[..Math.Min(joined.Length, MaximumBatchCharacters)],
                                        settings.UiLanguage,
                                        rangeLabel,
                                        IsConsolidation: true),
                                    timeout.Token),
                                timeout.Token)
                            .ConfigureAwait(false),
                    ];
                    break;
                }

                partials = next;
            }

            var outputPath = await historyStore.WriteSummaryAsync(
                    settings.AiHistoryDirectory,
                    range,
                    documents.Count,
                    partials[0],
                    DateTimeOffset.Now,
                    timeout.Token)
                .ConfigureAwait(false);
            return new AiSummaryGenerationResult(true, documents.Count, outputPath);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new AiSummaryGenerationResult(false, 0, ErrorCode: "timeout");
        }
        catch (TranslationProviderException)
        {
            return new AiSummaryGenerationResult(false, 0, ErrorCode: "provider_failed");
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or ArgumentException
                                          or NotSupportedException)
        {
            return new AiSummaryGenerationResult(false, 0, ErrorCode: "directory_unavailable");
        }
        finally
        {
            _summaryConcurrency.Release();
        }
    }

    internal static IReadOnlyList<string> CreateBatches(IEnumerable<string> documents)
    {
        var batches = new List<string>();
        var current = new StringBuilder();
        foreach (var document in documents.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var remaining = document.Trim();
            while (remaining.Length > 0)
            {
                var available = MaximumBatchCharacters - current.Length;
                if (available < 256)
                {
                    batches.Add(current.ToString());
                    current.Clear();
                    available = MaximumBatchCharacters;
                }

                var take = Math.Min(available, remaining.Length);
                if (current.Length > 0)
                {
                    current.AppendLine().AppendLine("--- RECORD ---");
                    available = MaximumBatchCharacters - current.Length;
                    if (available <= 0)
                    {
                        batches.Add(current.ToString());
                        current.Clear();
                        continue;
                    }

                    take = Math.Min(available, remaining.Length);
                }

                current.Append(remaining.AsSpan(0, take));
                remaining = remaining[take..];
                if (current.Length >= MaximumBatchCharacters)
                {
                    batches.Add(current.ToString());
                    current.Clear();
                }
            }
        }

        if (current.Length > 0)
        {
            batches.Add(current.ToString());
        }

        return batches;
    }

    private static async Task<string> CollectAsync(
        IAsyncEnumerable<TranslationChunk> chunks,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        await foreach (var chunk in chunks.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            builder.Append(chunk.TextDelta);
        }

        if (builder.Length == 0)
        {
            throw new TranslationProviderException(
                "AI 总结未返回内容。",
                TranslationFailureKind.Server);
        }

        return builder.ToString().Trim();
    }
}
