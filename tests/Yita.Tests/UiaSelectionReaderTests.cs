using Yita.Selection;
using Yita.Models;

namespace Yita.Tests;

public sealed class UiaSelectionReaderTests
{
    [Theory]
    [InlineData(100, true)]
    [InlineData(101, false)]
    [InlineData(0, false)]
    public void EmbeddedBrowserRendererRequiresProvenWindowOwnership(int focusedWindow, bool expected)
    {
        Assert.Equal(expected, UiaSelectionReader.ShouldIncludeFocusedElementInTargetWindow(
            42, 43, new IntPtr(100), new IntPtr(focusedWindow), allowEmbeddedProcess: true));
    }

    [Theory]
    [InlineData(42, 42, true)]
    [InlineData(42, 43, false)]
    [InlineData(0, 0, false)]
    [InlineData(null, 42, false)]
    [InlineData(42, null, false)]
    public void FocusedElementMustBelongToHitElementProcess(
        int? hitProcessId,
        int? focusedProcessId,
        bool expected)
    {
        Assert.Equal(
            expected,
            UiaSelectionReader.ShouldIncludeFocusedElement(hitProcessId, focusedProcessId));
    }

    [Theory]
    [InlineData(42, 42, 100, 100, true)]
    [InlineData(42, 42, 100, 0, true)]
    [InlineData(42, 42, 100, 101, false)]
    [InlineData(42, 43, 100, 100, false)]
    public void FocusedDocumentWithoutNativeWindowHandleCanStillBeUsed(
        int? hitProcessId,
        int? focusedProcessId,
        int expectedRootOwner,
        int focusedRootOwner,
        bool expected)
    {
        Assert.Equal(
            expected,
            UiaSelectionReader.ShouldIncludeFocusedElementInTargetWindow(
                hitProcessId,
                focusedProcessId,
                new IntPtr(expectedRootOwner),
                new IntPtr(focusedRootOwner)));
    }

    [Fact]
    public async Task PhysicalReadsAreBoundedWhenProvidersRemainBlocked()
    {
        using var readsStarted = new CountdownEvent(2);
        using var releaseReads = new ManualResetEventSlim();
        var callCount = 0;
        var reader = new UiaSelectionReader(
            (_, _, _) =>
            {
                var callNumber = Interlocked.Increment(ref callCount);
                if (callNumber <= 2)
                {
                    readsStarted.Signal();
                }
                releaseReads.Wait();
                return new SelectionCapture("selected");
            },
            point => (uint)point.X);

        var first = reader.TryReadSelectionAsync(
            new ScreenPoint(10, 10),
            includeContext: false,
            CancellationToken.None);
        var second = reader.TryReadSelectionAsync(
            new ScreenPoint(20, 20),
            includeContext: false,
            CancellationToken.None);

        Assert.True(readsStarted.Wait(TimeSpan.FromSeconds(2)));
        var saturatedResult = await reader.TryReadSelectionAsync(
            new ScreenPoint(30, 30),
            includeContext: false,
            CancellationToken.None);

        Assert.Null(saturatedResult);
        Assert.Equal(2, Volatile.Read(ref callCount));

        releaseReads.Set();
        Assert.Equal("selected", (await first)?.Text);
        Assert.Equal("selected", (await second)?.Text);

        var recovered = await reader.TryReadSelectionAsync(
            new ScreenPoint(40, 40),
            includeContext: false,
            CancellationToken.None);
        Assert.Equal("selected", recovered?.Text);
        Assert.Equal(3, Volatile.Read(ref callCount));
    }

    [Fact]
    public async Task CallerCancellationDoesNotFreeSlotBeforePhysicalReadCompletes()
    {
        using var readsStarted = new CountdownEvent(2);
        using var readsCompleted = new CountdownEvent(2);
        using var releaseReads = new ManualResetEventSlim();
        var reader = new UiaSelectionReader(
            (_, _, _) =>
            {
                readsStarted.Signal();
                releaseReads.Wait();
                readsCompleted.Signal();
                return null;
            },
            point => (uint)point.X);
        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();

        var first = reader.TryReadSelectionAsync(
            new ScreenPoint(10, 10),
            includeContext: false,
            firstCancellation.Token);
        var second = reader.TryReadSelectionAsync(
            new ScreenPoint(20, 20),
            includeContext: false,
            secondCancellation.Token);
        Assert.True(readsStarted.Wait(TimeSpan.FromSeconds(2)));

        firstCancellation.Cancel();
        secondCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);

        var stillSaturated = await reader.TryReadSelectionAsync(
            new ScreenPoint(30, 30),
            includeContext: false,
            CancellationToken.None);
        Assert.Null(stillSaturated);

        releaseReads.Set();
        Assert.True(readsCompleted.Wait(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task BlockedTargetProcessCannotConsumeBothGlobalReadSlots()
    {
        using var blockedReadStarted = new ManualResetEventSlim();
        using var releaseBlockedRead = new ManualResetEventSlim();
        var reader = new UiaSelectionReader(
            (point, _, _) =>
            {
                if (point.X < 100)
                {
                    blockedReadStarted.Set();
                    releaseBlockedRead.Wait();
                    return null;
                }

                return new SelectionCapture("healthy");
            },
            point => point.X < 100 ? 41u : 42u);

        var blocked = reader.TryReadSelectionAsync(
            new ScreenPoint(10, 10),
            includeContext: false,
            CancellationToken.None);
        Assert.True(blockedReadStarted.Wait(TimeSpan.FromSeconds(2)));

        var duplicateBlocked = await reader.TryReadSelectionAsync(
            new ScreenPoint(20, 20),
            includeContext: false,
            CancellationToken.None);
        var healthy = await reader.TryReadSelectionAsync(
            new ScreenPoint(120, 20),
            includeContext: false,
            CancellationToken.None);

        Assert.Null(duplicateBlocked);
        Assert.Equal("healthy", healthy?.Text);
        Assert.Equal(1, reader.ActiveReadCount);
        Assert.Equal(1, reader.RejectedReadCount);

        releaseBlockedRead.Set();
        await blocked;
    }
}
