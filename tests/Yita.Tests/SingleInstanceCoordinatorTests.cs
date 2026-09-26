using Yita.Services;
using System.Diagnostics;

namespace Yita.Tests;

public sealed class SingleInstanceCoordinatorTests
{
    [Fact]
    public void FirstInstance_IsPrimary_AndSecondInstanceIsNot()
    {
        var instanceName = CreateUniqueInstanceName();
        using var primary = SingleInstanceCoordinator.Acquire(instanceName);
        using var secondary = SingleInstanceCoordinator.Acquire(instanceName);

        Assert.True(primary.IsPrimary);
        Assert.False(secondary.IsPrimary);
    }

    [Fact]
    public void SecondInstance_SignalsPrimary_WhenListenerIsAlreadyRunning()
    {
        var instanceName = CreateUniqueInstanceName();
        using var activationReceived = new ManualResetEventSlim();
        using var primary = SingleInstanceCoordinator.Acquire(instanceName);
        primary.StartListening(activationReceived.Set);

        using var secondary = SingleInstanceCoordinator.Acquire(instanceName);

        Assert.False(secondary.IsPrimary);
        Assert.True(activationReceived.Wait(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void ActivationSignal_IsRetained_UntilPrimaryStartsListening()
    {
        var instanceName = CreateUniqueInstanceName();
        using var activationReceived = new ManualResetEventSlim();
        using var primary = SingleInstanceCoordinator.Acquire(instanceName);
        using var secondary = SingleInstanceCoordinator.Acquire(instanceName);

        primary.StartListening(activationReceived.Set);

        Assert.True(activationReceived.Wait(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Dispose_ReleasesInstanceNameForANewPrimary()
    {
        var instanceName = CreateUniqueInstanceName();
        var first = SingleInstanceCoordinator.Acquire(instanceName);
        Assert.True(first.IsPrimary);

        first.Dispose();
        first.Dispose();

        using var replacement = SingleInstanceCoordinator.Acquire(instanceName);
        Assert.True(replacement.IsPrimary);
    }

    [Fact]
    public async Task TakeoverRetry_BecomesPrimaryWhenPreviousInstanceFinishesExiting()
    {
        var instanceName = CreateUniqueInstanceName();
        var primary = SingleInstanceCoordinator.Acquire(instanceName);
        var releaseTask = Task.Run(async () =>
        {
            await Task.Delay(180);
            primary.Dispose();
        });

        using var replacement = SingleInstanceCoordinator.AcquireWithTakeoverRetry(
            instanceName,
            retryCount: 30,
            retryDelay: TimeSpan.FromMilliseconds(40));

        Assert.True(replacement.IsPrimary);
        await releaseTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void TakeoverRetry_RemainsSecondaryWhilePrimaryIsHealthy()
    {
        var instanceName = CreateUniqueInstanceName();
        using var primary = SingleInstanceCoordinator.Acquire(instanceName);
        using var secondary = SingleInstanceCoordinator.AcquireWithTakeoverRetry(
            instanceName,
            retryCount: 2,
            retryDelay: TimeSpan.FromMilliseconds(1));

        Assert.True(primary.IsPrimary);
        Assert.False(secondary.IsPrimary);
    }

    [Fact]
    public void TakeoverRetry_ReturnsPromptlyAfterPrimaryAcknowledgesActivation()
    {
        var instanceName = CreateUniqueInstanceName();
        using var primary = SingleInstanceCoordinator.Acquire(instanceName);
        primary.StartListening(() => { });
        var startedAt = Stopwatch.GetTimestamp();

        using var secondary = SingleInstanceCoordinator.AcquireWithTakeoverRetry(
            instanceName,
            retryCount: 30,
            retryDelay: TimeSpan.FromMilliseconds(120));

        Assert.False(secondary.IsPrimary);
        Assert.True(secondary.WasActivationAcknowledged);
        Assert.True(Stopwatch.GetElapsedTime(startedAt) < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void SecondaryInstance_CannotStartListener()
    {
        var instanceName = CreateUniqueInstanceName();
        using var primary = SingleInstanceCoordinator.Acquire(instanceName);
        using var secondary = SingleInstanceCoordinator.Acquire(instanceName);

        Assert.Throws<InvalidOperationException>(() => secondary.StartListening(() => { }));
    }

    [Fact]
    public void DefaultName_ContainsCurrentSessionId()
    {
        var defaultName = SingleInstanceCoordinator.CreateDefaultInstanceName();

        Assert.Contains($"Session{System.Diagnostics.Process.GetCurrentProcess().SessionId}", defaultName);
    }

    private static string CreateUniqueInstanceName()
    {
        return $"Yita.Tests.{Guid.NewGuid():N}";
    }
}
