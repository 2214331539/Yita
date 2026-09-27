namespace Yita.Selection;

internal readonly record struct ClipboardStamp(uint Sequence, IntPtr Owner, bool OwnerAllowed);
internal readonly record struct ClipboardCopyResult(string? Text, uint? Sequence, bool Changed, IntPtr Owner);

/// <summary>Waits for one owned copy, including delayed rendering of its text format.</summary>
internal static class ClipboardCopyWaiter
{
    internal static async Task<ClipboardCopyResult> WaitAsync(
        uint originalSequence, TimeSpan timeout,
        Func<ClipboardStamp> getStamp, Func<string?> readText,
        Func<TimeSpan, Task>? delay = null)
    {
        delay ??= Task.Delay;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        IntPtr owner = IntPtr.Zero;
        uint? observedSequence = null;
        while (timer.Elapsed < timeout)
        {
            var before = getStamp();
            if (before.Sequence != originalSequence)
            {
                if (!before.OwnerAllowed || before.Owner == IntPtr.Zero
                    || (owner != IntPtr.Zero && owner != before.Owner))
                    return new(null, null, true, IntPtr.Zero);
                owner = before.Owner;
                observedSequence = before.Sequence;
                var text = readText();
                var after = getStamp();
                if (!after.OwnerAllowed || after.Owner != owner)
                    return new(null, null, true, IntPtr.Zero);
                // Getting delayed text can itself advance the sequence. Re-read
                // a stable snapshot rather than rejecting the entire gesture.
                observedSequence = after.Sequence;
                if (after.Sequence == before.Sequence && !string.IsNullOrWhiteSpace(text))
                    return new(text, after.Sequence, true, owner);
            }
            await delay(TimeSpan.FromMilliseconds(20));
        }
        var final = getStamp();
        return observedSequence is not null && final.Sequence == observedSequence
            && final.OwnerAllowed && final.Owner == owner
                ? new(null, final.Sequence, true, owner)
                : new(null, null, observedSequence is not null || final.Sequence != originalSequence, IntPtr.Zero);
    }
}
