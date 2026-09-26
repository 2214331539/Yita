using Yita.Translation;

namespace Yita.Tests;

public sealed class ProviderCircuitBreakerTests
{
    [Fact]
    public void ThreeTransientFailures_OpenCircuitUntilCooldownExpires()
    {
        var now = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
        var breaker = new ProviderCircuitBreaker(
            failureThreshold: 3,
            breakDuration: TimeSpan.FromSeconds(6),
            utcNow: () => now);
        var failure = new TranslationProviderException(
            "network",
            TranslationFailureKind.Connectivity);

        breaker.RecordFailure(failure);
        breaker.RecordFailure(failure);
        breaker.ThrowIfOpen();
        breaker.RecordFailure(failure);

        var openException = Assert.Throws<TranslationProviderException>(breaker.ThrowIfOpen);
        Assert.True(openException.IsTransient);

        now = now.AddSeconds(6);
        breaker.ThrowIfOpen();
    }

    [Fact]
    public void SuccessOrNonTransientFailure_ResetsTransientSequence()
    {
        var breaker = new ProviderCircuitBreaker(failureThreshold: 2);
        var transient = new TranslationProviderException(
            "network",
            TranslationFailureKind.Connectivity);
        breaker.RecordFailure(transient);
        breaker.RecordSuccess();
        breaker.RecordFailure(transient);
        breaker.ThrowIfOpen();

        breaker.RecordFailure(new TranslationProviderException(
            "invalid key",
            TranslationFailureKind.Authentication));
        breaker.RecordFailure(transient);

        breaker.ThrowIfOpen();
    }

    [Theory]
    [InlineData((int)TranslationFailureKind.Connectivity, true)]
    [InlineData((int)TranslationFailureKind.Timeout, true)]
    [InlineData((int)TranslationFailureKind.RateLimit, true)]
    [InlineData((int)TranslationFailureKind.Server, true)]
    [InlineData((int)TranslationFailureKind.Authentication, false)]
    [InlineData((int)TranslationFailureKind.Protocol, false)]
    public void FailureClassification_IsExplicit(int kindValue, bool expected)
    {
        var kind = (TranslationFailureKind)kindValue;
        Assert.Equal(expected, new TranslationProviderException("failure", kind).IsTransient);
    }
}
