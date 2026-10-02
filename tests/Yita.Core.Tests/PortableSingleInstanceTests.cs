using Yita.Core.Platform;

namespace Yita.Core.Tests;

public sealed class PortableSingleInstanceTests
{
    [Fact]
    public async Task LockIsExclusiveAndBackgroundLaunchDoesNotActivateTheOwner()
    {
        var directory = TemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "instance.lock");
            using var owner = new PortableSingleInstanceGuard(path);
            var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            owner.StartActivationListener(() => activated.TrySetResult());
            using (var background = new PortableSingleInstanceGuard(path, requestActivation: false))
                Assert.False(background.IsOwner);
            Assert.True(owner.IsOwner);
            await Task.Delay(50);
            Assert.False(activated.Task.IsCompleted);
            owner.Dispose();
            using var replacement = new PortableSingleInstanceGuard(path, requestActivation: false);
            Assert.True(replacement.IsOwner);
            Assert.True(File.Exists(path));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ActivationBeforeListenerStartsIsDeliveredAndDuplicateListenerIsIgnored()
    {
        var directory = TemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "instance.lock");
            using var owner = new PortableSingleInstanceGuard(path);
            using var second = new PortableSingleInstanceGuard(path);
            Assert.False(second.IsOwner);
            var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            owner.StartActivationListener(() => activated.TrySetResult());
            owner.StartActivationListener(() => throw new InvalidOperationException("Duplicate listener"));
            await activated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task FailingActivationCallbackDoesNotPreventTheNextRequest()
    {
        var directory = TemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "instance.lock");
            using var owner = new PortableSingleInstanceGuard(path);
            var calls = 0;
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            owner.StartActivationListener(() =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    first.TrySetResult();
                    throw new InvalidOperationException("Test callback failure");
                }
                next.TrySetResult();
            });
            using var second = new PortableSingleInstanceGuard(path);
            await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var third = new PortableSingleInstanceGuard(path);
            await next.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, calls);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void InvalidLockPathIsReportedInsteadOfTreatingItAsAnotherInstance()
    {
        var directory = TemporaryDirectory();
        try
        {
            var exception = Record.Exception(() => new PortableSingleInstanceGuard(directory));
            Assert.True(exception is IOException or UnauthorizedAccessException);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string TemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "yita-instance-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
