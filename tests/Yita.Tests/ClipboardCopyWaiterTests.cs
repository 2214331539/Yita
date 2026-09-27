using Yita.Selection;

namespace Yita.Tests;

public sealed class ClipboardCopyWaiterTests
{
    [Fact]
    public async Task DelayedTextRenderingMayAdvanceSequenceDuringRead()
    {
        uint sequence = 2;
        var reads = 0;
        var result = await ClipboardCopyWaiter.WaitAsync(1, TimeSpan.FromSeconds(1),
            () => new(sequence, (IntPtr)10, true),
            () => { reads++; if (reads == 1) sequence++; return "PDF text"; },
            _ => Task.CompletedTask);
        Assert.Equal("PDF text", result.Text);
        Assert.Equal(3u, result.Sequence);
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task EmptyFormatsThenTextFromSameOwnerAreAccepted()
    {
        uint sequence = 2;
        var result = await ClipboardCopyWaiter.WaitAsync(1, TimeSpan.FromSeconds(1),
            () => new(sequence, (IntPtr)10, true),
            () => sequence == 2 ? null : "ready",
            _ => { sequence = 3; return Task.CompletedTask; });
        Assert.Equal("ready", result.Text);
        Assert.Equal(3u, result.Sequence);
    }

    [Fact]
    public async Task ForeignClipboardOwnerIsNeverReadOrRestored()
    {
        var read = false;
        var result = await ClipboardCopyWaiter.WaitAsync(1, TimeSpan.FromSeconds(1),
            () => new(2, (IntPtr)20, false),
            () => { read = true; return "unrelated"; });
        Assert.False(read);
        Assert.Null(result.Text);
        Assert.Null(result.Sequence);
        Assert.True(result.Changed);
    }

    [Fact]
    public async Task OwnerChangeDuringReadAbandonsTransactionEvenWithinAllowedProcesses()
    {
        IntPtr owner = (IntPtr)10;
        var result = await ClipboardCopyWaiter.WaitAsync(1, TimeSpan.FromSeconds(1),
            () => new(2, owner, true),
            () => { owner = (IntPtr)11; return "another copy"; });
        Assert.Null(result.Text);
        Assert.Null(result.Sequence);
        Assert.True(result.Changed);
    }

    [Fact]
    public async Task UnchangedClipboardNeverReusesOldText()
    {
        var read = false;
        var result = await ClipboardCopyWaiter.WaitAsync(7, TimeSpan.FromMilliseconds(25),
            () => new(7, (IntPtr)10, true),
            () => { read = true; return "old clipboard"; });
        Assert.False(read);
        Assert.False(result.Changed);
        Assert.Null(result.Sequence);
    }

    [Fact]
    public async Task OwnedEmptyCopyCanBeRestoredButMustNotTriggerAnotherCopy()
    {
        var result = await ClipboardCopyWaiter.WaitAsync(1, TimeSpan.FromMilliseconds(25),
            () => new(2, (IntPtr)10, true), () => null);
        Assert.Null(result.Text);
        Assert.True(result.Changed);
        Assert.Equal(2u, result.Sequence);
        Assert.Equal((IntPtr)10, result.Owner);
    }
}
