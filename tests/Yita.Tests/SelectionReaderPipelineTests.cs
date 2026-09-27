using Yita.Models;
using Yita.Selection;

namespace Yita.Tests;

public sealed class SelectionReaderPipelineTests
{
    [Fact]
    public async Task WpsCompatibilityUsesShortPrimaryBudgetAndCancelsSlowProvider()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var clipboard = new StubSelectionReader("WPS selection");
        var pipeline = new SelectionReaderPipeline(new BlockingSelectionReader(), new StubSelectionReader(null),
            clipboard, () => false, allowTargetClipboardFallback: _ => true);
        var result = await pipeline.TryReadSelectedTextAsync(new ScreenPoint(10, 20), CancellationToken.None);
        Assert.Equal("WPS selection", result);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(650), $"Read took {stopwatch.ElapsedMilliseconds} ms");
        Assert.Equal(1, clipboard.CallCount);
    }

    [Fact]
    public async Task TargetLosingCompatibilityDuringPrimaryReadDoesNotCopy()
    {
        var checks = 0;
        var clipboard = new StubSelectionReader("wrong target");
        var pipeline = new SelectionReaderPipeline(new StubSelectionReader(null), new StubSelectionReader(null),
            clipboard, () => false, allowTargetClipboardFallback: _ => ++checks == 1);
        Assert.Null(await pipeline.TryReadSelectedTextAsync(new ScreenPoint(10, 20), CancellationToken.None));
        Assert.Equal(0, clipboard.CallCount);
    }

    [Theory]
    [InlineData(true, "PDF selection", 1)]
    [InlineData(false, null, 0)]
    public async Task TargetCompatibilityDoesNotEnableFallbackForOtherApps(bool targetAllowed, string? expected, int calls)
    {
        var clipboard = new StubSelectionReader("PDF selection");
        var pipeline = new SelectionReaderPipeline(new StubSelectionReader(null), new StubSelectionReader(null),
            clipboard, () => false, allowTargetClipboardFallback: _ => targetAllowed);
        Assert.Equal(expected, await pipeline.TryReadSelectedTextAsync(new ScreenPoint(10, 20), CancellationToken.None));
        Assert.Equal(calls, clipboard.CallCount);
    }

    [Fact]
    public async Task AccessibleWpsSelectionDoesNotTouchClipboard()
    {
        var clipboard = new StubSelectionReader("clipboard");
        var pipeline = new SelectionReaderPipeline(new StubSelectionReader("accessible"), new StubSelectionReader(null),
            clipboard, () => false, allowTargetClipboardFallback: _ => true);
        Assert.Equal("accessible", await pipeline.TryReadSelectedTextAsync(new ScreenPoint(10, 20), CancellationToken.None));
        Assert.Equal(0, clipboard.CallCount);
    }

    [Fact]
    public async Task ReturnsUiaTextWithoutInvokingFallback()
    {
        var primary = new StubSelectionReader("来自 UI Automation");
        var fallback = new StubSelectionReader("来自剪贴板");
        var pipeline = new SelectionReaderPipeline(primary, fallback);

        var result = await pipeline.TryReadSelectedTextAsync(new ScreenPoint(10, 20), CancellationToken.None);

        Assert.Equal("来自 UI Automation", result);
        Assert.Equal(1, primary.CallCount);
        Assert.Equal(0, fallback.CallCount);
    }

    [Fact]
    public async Task UsesFallbackWhenUiaReturnsNoText()
    {
        var primary = new StubSelectionReader(null);
        var fallback = new StubSelectionReader("来自剪贴板");
        var pipeline = new SelectionReaderPipeline(primary, fallback);

        var result = await pipeline.TryReadSelectedTextAsync(new ScreenPoint(10, 20), CancellationToken.None);

        Assert.Equal("来自剪贴板", result);
        Assert.Equal(1, primary.CallCount);
        Assert.Equal(1, fallback.CallCount);
    }

    [Fact]
    public async Task UsesFallbackWhenUiaReaderBlocks()
    {
        var primary = new BlockingSelectionReader();
        var fallback = new StubSelectionReader("来自剪贴板");
        var pipeline = new SelectionReaderPipeline(primary, fallback);

        var result = await pipeline.TryReadSelectedTextAsync(new ScreenPoint(10, 20), CancellationToken.None);

        Assert.Equal("来自剪贴板", result);
        Assert.Equal(1, fallback.CallCount);
    }

    [Fact]
    public async Task UsesFallbackWhenPrimaryProviderThrows()
    {
        var fallback = new StubSelectionReader("来自安全回退");
        var pipeline = new SelectionReaderPipeline(
            new ThrowingSelectionReader(),
            fallback);

        var result = await pipeline.TryReadSelectedTextAsync(
            new ScreenPoint(10, 20),
            CancellationToken.None);

        Assert.Equal("来自安全回退", result);
        Assert.Equal(1, fallback.CallCount);
    }

    [Fact]
    public async Task DoesNotInvokeClipboardFallbackWhenDisabled()
    {
        var primary = new StubSelectionReader(null);
        var nativeFallback = new StubSelectionReader(null);
        var clipboardFallback = new StubSelectionReader("来自剪贴板");
        var pipeline = new SelectionReaderPipeline(
            primary,
            nativeFallback,
            clipboardFallback,
            () => false);

        var result = await pipeline.TryReadSelectedTextAsync(new ScreenPoint(10, 20), CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(1, nativeFallback.CallCount);
        Assert.Equal(0, clipboardFallback.CallCount);
    }

    [Fact]
    public async Task InvokesClipboardFallbackOnlyWhenExplicitlyEnabled()
    {
        var primary = new StubSelectionReader(null);
        var nativeFallback = new StubSelectionReader(null);
        var clipboardFallback = new StubSelectionReader("来自剪贴板");
        var pipeline = new SelectionReaderPipeline(
            primary,
            nativeFallback,
            clipboardFallback,
            () => true);

        var result = await pipeline.TryReadSelectedTextAsync(new ScreenPoint(10, 20), CancellationToken.None);

        Assert.Equal("来自剪贴板", result);
        Assert.Equal(1, clipboardFallback.CallCount);
    }

    [Fact]
    public async Task RetriesSafeReadersForDelayedBrowserLikeSelectionBeforeClipboardFallback()
    {
        var primary = new SequenceSelectionReader(null, "延迟出现的 PDF 选区");
        var nativeFallback = new StubSelectionReader(null);
        var clipboardFallback = new StubSelectionReader("不应读取剪贴板");
        var delays = new List<TimeSpan>();
        var pipeline = new SelectionReaderPipeline(
            primary,
            nativeFallback,
            clipboardFallback,
            allowClipboardFallback: () => false,
            requiresStabilizedRead: _ => true,
            delayAsync: (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        var result = await pipeline.TryReadSelectedTextAsync(
            new ScreenPoint(10, 20),
            CancellationToken.None);

        Assert.Equal("延迟出现的 PDF 选区", result);
        Assert.Equal(2, primary.CallCount);
        Assert.Equal(1, nativeFallback.CallCount);
        Assert.Single(delays);
        Assert.Equal(TimeSpan.FromMilliseconds(140), delays[0]);
        Assert.Equal(0, clipboardFallback.CallCount);
    }

    [Fact]
    public async Task UsesSecondStabilizationRetryForLatePdfAccessibilitySelection()
    {
        var primary = new SequenceSelectionReader(null, null, "PDF selection");
        var delays = new List<TimeSpan>();
        var pipeline = new SelectionReaderPipeline(
            primary,
            new StubSelectionReader(null),
            requiresStabilizedRead: _ => true,
            delayAsync: (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        var result = await pipeline.TryReadSelectedTextAsync(
            new ScreenPoint(10, 20),
            CancellationToken.None);

        Assert.Equal("PDF selection", result);
        Assert.Equal(3, primary.CallCount);
        Assert.Equal(
            [TimeSpan.FromMilliseconds(140), TimeSpan.FromMilliseconds(360)],
            delays);
    }

    [Fact]
    public void UiaAncestorSearchSupportsDeepChromiumPdfTrees()
    {
        Assert.True(UiaSelectionReader.MaximumAncestorDepth >= 32);
    }

    [Fact]
    public async Task ContextualPrimary_PreservesContextOnlyWhenRequested()
    {
        var primary = new ContextualStubSelectionReader();
        var pipeline = new SelectionReaderPipeline(primary, new StubSelectionReader(null));

        var withContext = await pipeline.TryReadSelectionAsync(
            new ScreenPoint(10, 20),
            includeContext: true,
            CancellationToken.None);
        var withoutContext = await pipeline.TryReadSelectionAsync(
            new ScreenPoint(10, 20),
            includeContext: false,
            CancellationToken.None);

        Assert.Equal("selected", withContext?.Text);
        Assert.Equal("surrounding paragraph", withContext?.Context);
        Assert.Null(withoutContext?.Context);
        Assert.Equal(new[] { true, false }, primary.ContextRequests);
    }

    private sealed class StubSelectionReader : ISelectionReader
    {
        private readonly string? _text;

        public StubSelectionReader(string? text)
        {
            _text = text;
        }

        public int CallCount { get; private set; }

        public Task<string?> TryReadSelectedTextAsync(ScreenPoint point, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_text);
        }
    }

    private sealed class BlockingSelectionReader : ISelectionReader
    {
        public async Task<string?> TryReadSelectedTextAsync(ScreenPoint point, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return null;
        }
    }

    private sealed class SequenceSelectionReader : ISelectionReader
    {
        private readonly Queue<string?> _results;

        public SequenceSelectionReader(params string?[] results)
        {
            _results = new Queue<string?>(results);
        }

        public int CallCount { get; private set; }

        public Task<string?> TryReadSelectedTextAsync(
            ScreenPoint point,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_results.Count > 0 ? _results.Dequeue() : null);
        }
    }

    private sealed class ThrowingSelectionReader : ISelectionReader
    {
        public Task<string?> TryReadSelectedTextAsync(
            ScreenPoint point,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Simulated broken UI Automation provider.");
        }
    }

    private sealed class ContextualStubSelectionReader : IContextualSelectionReader
    {
        public List<bool> ContextRequests { get; } = [];

        public Task<string?> TryReadSelectedTextAsync(
            ScreenPoint point,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<string?>("selected");
        }

        public Task<SelectionCapture?> TryReadSelectionAsync(
            ScreenPoint point,
            bool includeContext,
            CancellationToken cancellationToken)
        {
            ContextRequests.Add(includeContext);
            return Task.FromResult<SelectionCapture?>(
                new SelectionCapture("selected", includeContext ? "surrounding paragraph" : null));
        }
    }
}
