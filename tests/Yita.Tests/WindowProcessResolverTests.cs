using Yita.Services;

namespace Yita.Tests;

public sealed class WindowProcessResolverTests
{
    [Theory]
    [InlineData("wps", "paper.pdf - WPS Office", true)]
    [InlineData("WPS", "PAPER.PDF", true)]
    [InlineData("wpspdf", "document", true)]
    [InlineData("kpdf", "document", true)]
    [InlineData("wps", "notes.docx - WPS", false)]
    [InlineData("pwsh", "paper.pdf", false)]
    [InlineData("chrome", "WPS paper.pdf", false)]
    public void AutomaticCompatibilityOnlyTargetsWpsPdf(string process, string title, bool expected)
        => Assert.Equal(expected, WindowProcessResolver.IsWpsPdfProcess(process, title));

    [Theory]
    [InlineData("cmd")]
    [InlineData("PowerShell")]
    [InlineData("pwsh")]
    [InlineData("WindowsTerminal")]
    [InlineData("conhost")]
    public void ClipboardFallbackRejectsTerminalProcesses(string processName)
    {
        Assert.False(WindowProcessResolver.IsClipboardFallbackProcessAllowed(processName));
    }

    [Theory]
    [InlineData("WeChat")]
    [InlineData("chrome")]
    [InlineData("notepad")]
    public void ClipboardFallbackAllowsOrdinaryProcesses(string processName)
    {
        Assert.True(WindowProcessResolver.IsClipboardFallbackProcessAllowed(processName));
    }

    [Theory]
    [InlineData("msedge")]
    [InlineData("chrome")]
    [InlineData("zotero")]
    public void DelaysReadForBrowserAndPdfReaderProcesses(string processName)
    {
        Assert.True(WindowProcessResolver.RequiresSelectionStabilization(processName));
    }

    [Theory]
    [InlineData("notepad")]
    [InlineData("WeChat")]
    [InlineData(null)]
    public void DoesNotDelayReadForOrdinaryProcesses(string? processName)
    {
        Assert.False(WindowProcessResolver.RequiresSelectionStabilization(processName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ClipboardFallbackRejectsMissingProcessNames(string? processName)
    {
        Assert.False(WindowProcessResolver.IsClipboardFallbackProcessAllowed(processName));
    }

    [Fact]
    public void SelectionAllowsDifferentRendererProcessesInsideOneOwnedWindowTree()
    {
        var first = new SelectionWindowTarget(new IntPtr(100), 200);
        var second = new SelectionWindowTarget(new IntPtr(100), 300);

        Assert.True(WindowProcessResolver.AreTargetsInSameExternalWindow(
            first,
            second,
            currentProcessId: 999));
    }

    [Fact]
    public void SelectionRejectsTwoWindowsEvenWhenTheyBelongToOneProcess()
    {
        var first = new SelectionWindowTarget(new IntPtr(100), 200);
        var second = new SelectionWindowTarget(new IntPtr(101), 200);

        Assert.False(WindowProcessResolver.AreTargetsInSameExternalWindow(
            first,
            second,
            currentProcessId: 999));
    }

    [Theory]
    [InlineData(0, 200, 100, 200)]
    [InlineData(100, 999, 100, 200)]
    [InlineData(100, 200, 100, 999)]
    public void SelectionRejectsMissingOrCurrentProcessRoots(
        int firstRoot,
        uint firstProcess,
        int secondRoot,
        uint secondProcess)
    {
        var first = new SelectionWindowTarget(new IntPtr(firstRoot), firstProcess);
        var second = new SelectionWindowTarget(new IntPtr(secondRoot), secondProcess);

        Assert.False(WindowProcessResolver.AreTargetsInSameExternalWindow(
            first,
            second,
            currentProcessId: 999));
    }
}
