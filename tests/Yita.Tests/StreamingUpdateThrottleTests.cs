using Yita.Translation;

namespace Yita.Tests;

public sealed class StreamingUpdateThrottleTests
{
    [Fact]
    public void FirstNonEmptyContent_IsPublishedImmediately()
    {
        var now = DateTimeOffset.UnixEpoch;
        var throttle = new StreamingUpdateThrottle(utcNow: () => now);

        Assert.False(throttle.ShouldPublish(0));
        Assert.True(throttle.ShouldPublish(1));
        Assert.Equal(1, throttle.LastPublishedLength);
    }

    [Fact]
    public void SmallDeltas_AreCoalescedUntilCharacterThreshold()
    {
        var now = DateTimeOffset.UnixEpoch;
        var throttle = new StreamingUpdateThrottle(utcNow: () => now);
        Assert.True(throttle.ShouldPublish(1));

        Assert.False(throttle.ShouldPublish(32));
        Assert.True(throttle.HasPendingUpdate(32));
        Assert.True(throttle.ShouldPublish(33));
        Assert.False(throttle.HasPendingUpdate(33));
    }

    [Fact]
    public void SmallDeltas_ArePublishedWhenTimeThresholdElapses()
    {
        var now = DateTimeOffset.UnixEpoch;
        var throttle = new StreamingUpdateThrottle(utcNow: () => now);
        Assert.True(throttle.ShouldPublish(1));

        now = now.AddMilliseconds(34);
        Assert.False(throttle.ShouldPublish(2));

        now = now.AddMilliseconds(1);
        Assert.True(throttle.ShouldPublish(3));
    }

    [Fact]
    public void FinalUpdate_FlushesPendingContentBeforeThresholds()
    {
        var now = DateTimeOffset.UnixEpoch;
        var throttle = new StreamingUpdateThrottle(utcNow: () => now);
        Assert.True(throttle.ShouldPublish(10));
        Assert.False(throttle.ShouldPublish(11));

        Assert.True(throttle.ShouldPublish(11, isFinal: true));
        Assert.Equal(11, throttle.LastPublishedLength);
    }

    [Fact]
    public void FinalMarker_ForcesAFlushEvenWhenLengthIsUnchanged()
    {
        var throttle = new StreamingUpdateThrottle();
        Assert.True(throttle.ShouldPublish(10));

        Assert.True(throttle.ShouldPublish(10, isFinal: true));
    }

    [Fact]
    public void ShorterReplacement_IsPublishedImmediately()
    {
        var now = DateTimeOffset.UnixEpoch;
        var throttle = new StreamingUpdateThrottle(utcNow: () => now);
        Assert.True(throttle.ShouldPublish(50));

        Assert.True(throttle.ShouldPublish(5));
        Assert.Equal(5, throttle.LastPublishedLength);
    }

    [Fact]
    public void Reset_MakesNextContentAnImmediateFirstUpdate()
    {
        var throttle = new StreamingUpdateThrottle();
        Assert.True(throttle.ShouldPublish(20));
        throttle.Reset();

        Assert.Equal(0, throttle.LastPublishedLength);
        Assert.True(throttle.ShouldPublish(1));
    }

    [Fact]
    public void ConstructorAndMethods_RejectInvalidValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new StreamingUpdateThrottle(minimumInterval: TimeSpan.FromMilliseconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new StreamingUpdateThrottle(minimumCharacterDelta: 0));

        var throttle = new StreamingUpdateThrottle();
        Assert.Throws<ArgumentOutOfRangeException>(() => throttle.ShouldPublish(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => throttle.HasPendingUpdate(-1));
    }
}
