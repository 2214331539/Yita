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
}
