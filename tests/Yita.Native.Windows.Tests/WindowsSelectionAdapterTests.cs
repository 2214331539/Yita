using Yita.Core.Selection;
using Yita.Native.Windows;

namespace Yita.Native.Windows.Tests;

public sealed class WindowsSelectionAdapterTests
{
    [Fact]
    public async Task AdapterPreservesThePlatformNeutralSelectionContract()
    {
        var adapter = new WindowsSelectionAdapter((request, _) => Task.FromResult(
            new SelectionResult(
                "selected text",
                SelectionSource.Accessibility,
                new SelectionBounds(request.Pointer.X, request.Pointer.Y, 40, 18))));

        var result = await adapter.ReadAsync(new SelectionRequest(
            SelectionTrigger.TranslateShortcut,
            new ScreenPoint(120, 80)));

        Assert.True(result.Succeeded);
        Assert.Equal("selected text", result.Text);
        Assert.Equal(120, result.Bounds?.X);
    }

    [Fact]
    public void RuntimeRequiresWindowsBeforeStartingNativeHooks()
    {
        using var runtime = new WindowsSelectionRuntime();
        if (!OperatingSystem.IsWindows())
            Assert.Throws<PlatformNotSupportedException>(() => runtime.Start());
    }

    [Fact]
    public void MouseSelectionRequiresWindowsBeforeStartingNativeHooks()
    {
        using var service = new WindowsMouseSelectionService();
        if (!OperatingSystem.IsWindows())
            Assert.Throws<PlatformNotSupportedException>(() => service.Start());
    }

    [Fact]
    public void SecretStoreFactoryUsesTheNativeStoreOnWindows()
    {
        var store = SecretStoreFactory.CreateDefault();

        if (OperatingSystem.IsWindows())
            Assert.IsType<WindowsCredentialSecretStore>(store);
        else
            Assert.IsType<Yita.Core.Settings.MemorySecretStore>(store);
    }

    [Fact]
    public async Task NativeControlReaderFailsClosedOutsideWindows()
    {
        var reader = new WindowsNativeControlSelectionReader();
        var result = await reader.ReadAsync(new SelectionRequest(
            SelectionTrigger.TranslateShortcut,
            new ScreenPoint(10, 20)));

        if (!OperatingSystem.IsWindows())
            Assert.Equal(SelectionFailureKind.UnsupportedApplication, result.Failure);
    }

    [Fact]
    public async Task UiAutomationReaderUsesWorkerBeforeOtherReaders()
    {
        var worker = new StubUiAutomationWorker(new SelectionResult(
            "来自 UI Automation",
            SelectionSource.Accessibility,
            new SelectionBounds(10, 20, 80, 18)));
        var reader = new WindowsUiAutomationSelectionReader(worker);

        var result = await reader.ReadAsync(new SelectionRequest(
            SelectionTrigger.TranslateShortcut,
            new ScreenPoint(10, 20)));

        if (OperatingSystem.IsWindows())
        {
            Assert.True(result.Succeeded);
            Assert.Equal("来自 UI Automation", result.Text);
        }
        else
        {
            Assert.Equal(SelectionFailureKind.UnsupportedApplication, result.Failure);
            Assert.Equal(0, worker.ReadCount);
        }
    }

    private sealed class StubUiAutomationWorker(SelectionResult result) : IWindowsUiAutomationWorker
    {
        public int ReadCount { get; private set; }

        public bool IsAvailable => true;

        public Task<SelectionResult> ReadAsync(
            SelectionRequest request,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return Task.FromResult(result);
        }

        public void Dispose() { }
    }
}
