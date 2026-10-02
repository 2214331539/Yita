using Yita.Core.Selection;
using Yita.Native.Windows;

namespace Yita.Native.Windows.Tests;

public sealed class WindowsSelectionAdapterTests
{
    [Fact]
    public async Task CopyablePdfUsesClipboardFallbackWhenAccessibilityIsEmpty()
    {
        var accessible = new StubReader(SelectionResult.Failed(SelectionFailureKind.Empty));
        var clipboard = new StubReader(new SelectionResult("PDF selection", SelectionSource.ClipboardFallback));
        using var adapter = new WindowsSelectionAdapter(accessible, clipboard);
        var result = await adapter.ReadAsync(new SelectionRequest(SelectionTrigger.MouseGesture, new ScreenPoint(10, 20), "AcroRd32"));
        Assert.Equal("PDF selection", result.Text);
        Assert.Equal(1, accessible.ReadCount);
        Assert.Equal(1, clipboard.ReadCount);
        adapter.UseClipboardFallback = false;
        Assert.False((await adapter.ReadAsync(new SelectionRequest(SelectionTrigger.MouseGesture, new ScreenPoint(10, 20)))).Succeeded);
        Assert.Equal(1, clipboard.ReadCount);
    }

    [Theory]
    [InlineData(SelectionFailureKind.ProtectedContent)]
    [InlineData(SelectionFailureKind.Cancelled)]
    public async Task ProtectedOrCancelledSelectionsNeverFallThroughToCopy(SelectionFailureKind failure)
    {
        var clipboard = new StubReader(new SelectionResult("must not copy", SelectionSource.ClipboardFallback));
        var safePipeline = new SelectionReaderPipeline(new ISelectionReader[]
        {
            new StubReader(SelectionResult.Failed(failure)), clipboard,
        });
        using var adapter = new WindowsSelectionAdapter(safePipeline, clipboard);
        var result = await adapter.ReadAsync(new SelectionRequest(SelectionTrigger.MouseGesture, new ScreenPoint(10, 20)));
        Assert.Equal(failure, result.Failure);
        Assert.Equal(0, clipboard.ReadCount);
    }

    [Fact]
    public async Task WpsCompatibilityCopiesFirstEvenWhenGenericFallbackIsDisabled()
    {
        var accessible = new StubReader(SelectionResult.Failed(SelectionFailureKind.Empty));
        var clipboard = new StubReader(new SelectionResult("WPS PDF selection", SelectionSource.ClipboardFallback));
        using var adapter = new WindowsSelectionAdapter(accessible, clipboard, _ => true) { UseClipboardFallback = false };
        Assert.True((await adapter.ReadAsync(new SelectionRequest(SelectionTrigger.MouseGesture, new ScreenPoint(10, 20)))).Succeeded);
        Assert.Equal(0, accessible.ReadCount);
        Assert.Equal(1, clipboard.ReadCount);
    }

    [Fact]
    public async Task BrowserWaitsForAccessibilitySelectionToStabilizeBeforeCopying()
    {
        var reads = 0;
        var accessible = new DelegateSelectionReader((_, _) => Task.FromResult(++reads == 1
            ? SelectionResult.Failed(SelectionFailureKind.Empty) : new SelectionResult("stabilized", SelectionSource.Accessibility)));
        var clipboard = new StubReader(SelectionResult.Failed(SelectionFailureKind.Empty));
        using var adapter = new WindowsSelectionAdapter(accessible, clipboard);
        var result = await adapter.ReadAsync(new SelectionRequest(SelectionTrigger.MouseGesture, new ScreenPoint(10, 20), "chrome"));
        Assert.Equal("stabilized", result.Text);
        Assert.Equal(2, reads);
        Assert.Equal(0, clipboard.ReadCount);
    }

    [Theory]
    [InlineData("wpspdf", "Document", true)]
    [InlineData("kpdf", "Document", true)]
    [InlineData("WPS", "Document.PDF", true)]
    [InlineData("wps", "pdf notes.docx", false)]
    [InlineData("chrome", "Document.pdf", false)]
    public void WpsPdfDetectionMatchesTheOriginalIncludingEmbeddedRenderers(string process, string title, bool expected) =>
        Assert.Equal(expected, WindowsClipboardSelectionReader.IsWpsPdfProcess(process, title));

    [Theory]
    [InlineData("WindowsTerminal", false)]
    [InlineData("pwsh", false)]
    [InlineData("wpspdf", true)]
    [InlineData("AcroRd32", true)]
    public void CopyFallbackAvoidsSendingInterruptCommandsToTerminals(string process, bool expected) =>
        Assert.Equal(expected, WindowsClipboardSelectionReader.IsClipboardFallbackProcessAllowed(process));

    private sealed class StubReader(SelectionResult result) : ISelectionReader
    {
        internal int ReadCount { get; private set; }
        public Task<SelectionResult> ReadAsync(SelectionRequest request, CancellationToken cancellationToken = default)
        { ReadCount++; return Task.FromResult(result); }
    }
    [Fact]
    public async Task ASecondLaunchActivatesTheExistingOwnerWithoutTakingItsMutex()
    {
        if (!OperatingSystem.IsWindows()) return;
        var identity = "Yita.Tests." + Guid.NewGuid().ToString("N");
        using var owner = new WindowsSingleInstanceGuard(identity);
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The activation event can arrive before the UI finishes starting.
        using var second = new WindowsSingleInstanceGuard(identity);
        Assert.True(owner.IsOwner);
        Assert.False(second.IsOwner);
        owner.StartActivationListener(() => requested.TrySetResult());
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
    [Fact]
    public void SendInputStructureMatchesTheWin32AbiIncludingTheLargestUnionMember()
    {
        Assert.Equal(IntPtr.Size == 8 ? 40 : 28, System.Runtime.InteropServices.Marshal.SizeOf<WindowsNativeMethods.Input>());
    }
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
