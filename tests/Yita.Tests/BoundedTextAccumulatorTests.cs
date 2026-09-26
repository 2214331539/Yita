using Yita.Selection;

namespace Yita.Tests;

public sealed class BoundedTextAccumulatorTests
{
    [Fact]
    public void Build_StaysWithinBudgetIncludingSeparators()
    {
        var accumulator = new BoundedTextAccumulator(10, "|");

        Assert.True(accumulator.TryAdd("abcd"));
        Assert.Equal(5, accumulator.MaximumNextPartLength);
        Assert.True(accumulator.TryAdd("123456789"));

        Assert.Equal("abcd|12345", accumulator.Build());
        Assert.Equal(0, accumulator.MaximumNextPartLength);
    }

    [Fact]
    public void TryAdd_IgnoresDuplicatePartsWithoutConsumingBudget()
    {
        var accumulator = new BoundedTextAccumulator(20, "\n");

        Assert.True(accumulator.TryAdd("same"));
        var remaining = accumulator.MaximumNextPartLength;
        Assert.False(accumulator.TryAdd("same"));

        Assert.Equal(remaining, accumulator.MaximumNextPartLength);
        Assert.Equal("same", accumulator.Build());
    }
}
