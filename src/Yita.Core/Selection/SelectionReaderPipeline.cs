namespace Yita.Core.Selection;

/// <summary>
/// Tries selection readers in priority order. A reader may return a structured
/// failure or throw for an unavailable provider; the next reader still gets a
/// chance. Cancellation is the only condition that stops the pipeline.
/// </summary>
public sealed class SelectionReaderPipeline : ISelectionReader
{
    private readonly IReadOnlyList<ISelectionReader> _readers;

    public SelectionReaderPipeline(IEnumerable<ISelectionReader> readers)
    {
        ArgumentNullException.ThrowIfNull(readers);
        _readers = readers.Where(static reader => reader is not null).ToArray();
        if (_readers.Count == 0) throw new ArgumentException("At least one selection reader is required.", nameof(readers));
    }

    public async Task<SelectionResult> ReadAsync(
        SelectionRequest request,
        CancellationToken cancellationToken = default)
    {
        SelectionResult? lastFailure = null;
        foreach (var reader in _readers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SelectionResult? result;
            try
            {
                result = await reader.ReadAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                result = SelectionResult.Failed(SelectionFailureKind.Unknown, "reader-exception");
            }

            if (result.Succeeded) return result;
            lastFailure = result;
        }

        return lastFailure ?? SelectionResult.Failed(SelectionFailureKind.Unknown, "no-reader");
    }
}
