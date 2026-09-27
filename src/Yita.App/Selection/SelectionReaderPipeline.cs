using Yita.Models;

namespace Yita.Selection;

internal sealed class SelectionReaderPipeline : IContextualSelectionReader
{
    private static readonly TimeSpan PrimaryReadTimeout = TimeSpan.FromMilliseconds(800);
    internal static readonly TimeSpan CompatibilityPrimaryReadTimeout = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan[] StabilizedReadRetryDelays =
    [
        TimeSpan.FromMilliseconds(140),
        TimeSpan.FromMilliseconds(360),
    ];
    private readonly ISelectionReader _primaryReader;
    private readonly ISelectionReader _fallbackReader;
    private readonly ISelectionReader? _clipboardFallbackReader;
    private readonly Func<bool> _allowClipboardFallback;
    private readonly Func<ScreenPoint, bool> _allowTargetClipboardFallback;
    private readonly Func<ScreenPoint, bool> _requiresStabilizedRead;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    public SelectionReaderPipeline(
        ISelectionReader primaryReader,
        ISelectionReader fallbackReader,
        ISelectionReader? clipboardFallbackReader = null,
        Func<bool>? allowClipboardFallback = null,
        Func<ScreenPoint, bool>? requiresStabilizedRead = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        Func<ScreenPoint, bool>? allowTargetClipboardFallback = null)
    {
        _primaryReader = primaryReader ?? throw new ArgumentNullException(nameof(primaryReader));
        _fallbackReader = fallbackReader ?? throw new ArgumentNullException(nameof(fallbackReader));
        _clipboardFallbackReader = clipboardFallbackReader;
        _allowClipboardFallback = allowClipboardFallback ?? (() => true);
        _allowTargetClipboardFallback = allowTargetClipboardFallback ?? (_ => false);
        _requiresStabilizedRead = requiresStabilizedRead ?? (_ => false);
        _delayAsync = delayAsync ?? Task.Delay;
    }

    public async Task<string?> TryReadSelectedTextAsync(
        ScreenPoint point,
        CancellationToken cancellationToken)
    {
        var capture = await TryReadSelectionAsync(point, includeContext: false, cancellationToken)
            .ConfigureAwait(false);
        return capture?.Text;
    }

    public async Task<SelectionCapture?> TryReadSelectionAsync(
        ScreenPoint point,
        bool includeContext,
        CancellationToken cancellationToken)
    {
        var targetCompatibility = _clipboardFallbackReader is not null && _allowTargetClipboardFallback(point);
        var primaryBudget = targetCompatibility ? CompatibilityPrimaryReadTimeout : PrimaryReadTimeout;
        var retryDelays = _requiresStabilizedRead(point)
            ? StabilizedReadRetryDelays
            : Array.Empty<TimeSpan>();
        for (var attempt = 0; ; attempt++)
        {
            var capture = await TryReadWithoutClipboardAsync(
                    point,
                    includeContext,
                    cancellationToken,
                    primaryBudget)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(capture?.Text))
            {
                return capture;
            }

            if (attempt >= retryDelays.Length
                || (_primaryReader is IsolatedUiaSelectionReader isolated && isolated.RequiresCompatibilityAt(point)))
            {
                break;
            }

            // Chromium/Electron PDF readers may commit their accessibility
            // selection a fraction after mouse-up. Retry only their safe,
            // non-clipboard paths before considering any compatibility fallback.
            await _delayAsync(retryDelays[attempt], cancellationToken).ConfigureAwait(false);
        }

        if (_clipboardFallbackReader is null || (!_allowClipboardFallback() && !_allowTargetClipboardFallback(point)))
        {
            return null;
        }

        var selectedText = await _clipboardFallbackReader
            .TryReadSelectedTextAsync(point, cancellationToken)
            .ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(selectedText)
            ? null
            : new SelectionCapture(selectedText);
    }

    private async Task<SelectionCapture?> TryReadWithoutClipboardAsync(
        ScreenPoint point,
        bool includeContext,
        CancellationToken cancellationToken,
        TimeSpan primaryBudget)
    {
        SelectionCapture? capture = null;
        using (var primaryTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            primaryTimeout.CancelAfter(primaryBudget);
            try
            {
                capture = await ReadCaptureAsync(
                        _primaryReader,
                        point,
                        includeContext,
                        primaryTimeout.Token)
                    .WaitAsync(primaryBudget, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                primaryTimeout.Cancel();
                if (_primaryReader is IsolatedUiaSelectionReader isolated) isolated.MarkUnresponsive(point);
                // Some applications expose UI Automation patterns that block
                // while their renderer is busy. Move on to the non-clipboard
                // reader instead of changing the user's clipboard.
            }
            catch (OperationCanceledException)
                when (primaryTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                if (_primaryReader is IsolatedUiaSelectionReader isolated) isolated.MarkUnresponsive(point);
                // The short primary-read budget expired; continue with the
                // non-clipboard reader.
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested
                                              && IsRecoverablePrimaryReaderFailure(exception))
            {
                // UI Automation is supplied by the target application. A bad
                // provider must not prevent the safe native fallback from
                // running for this gesture.
                System.Diagnostics.Debug.WriteLine(
                    $"Yita primary selection reader failed: {exception.GetType().Name}");
            }
        }

        if (!string.IsNullOrWhiteSpace(capture?.Text))
        {
            return capture;
        }

        var selectedText = await _fallbackReader
            .TryReadSelectedTextAsync(point, cancellationToken)
            .ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(selectedText)
            ? null
            : new SelectionCapture(selectedText);
    }

    private static async Task<SelectionCapture?> ReadCaptureAsync(
        ISelectionReader reader,
        ScreenPoint point,
        bool includeContext,
        CancellationToken cancellationToken)
    {
        if (reader is IContextualSelectionReader contextualReader)
        {
            return await contextualReader
                .TryReadSelectionAsync(point, includeContext, cancellationToken)
                .ConfigureAwait(false);
        }

        var text = await reader.TryReadSelectedTextAsync(point, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(text) ? null : new SelectionCapture(text);
    }

    private static bool IsRecoverablePrimaryReaderFailure(Exception exception)
    {
        return exception is not OutOfMemoryException
            and not AccessViolationException
            and not AppDomainUnloadedException
            and not BadImageFormatException;
    }
}
