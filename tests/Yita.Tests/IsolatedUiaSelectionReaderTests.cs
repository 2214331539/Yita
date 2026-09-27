using System.Diagnostics;
using System.IO;
using System.Text;
using Yita.Models;
using Yita.Selection;

namespace Yita.Tests;

public sealed class IsolatedUiaSelectionReaderTests
{
    private static ProcessStartInfo Helper(string body)
    {
        var script = "[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); [Console]::WriteLine('YITA-UIA-1'); " + body;
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"WindowsPowerShell\v1.0\powershell.exe"));
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-EncodedCommand");
        info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        return info;
    }

    [Fact]
    public async Task UnexpectedChildExitDoesNotKillParentAndEnablesFallbackForOnlyThatTarget()
    {
        var starts = 0;
        using var reader = new IsolatedUiaSelectionReader(() => {
            starts++;
            return Helper("$null = [Console]::ReadLine(); exit 37");
        }, point => (uint)point.X);
        await reader.WarmUpAsync();
        var clipboard = new TextReader("Recovered selection");
        var pipeline = new SelectionReaderPipeline(reader, new TextReader(null), clipboard, () => false,
            allowTargetClipboardFallback: reader.RequiresCompatibilityAt);
        Assert.Equal("Recovered selection", await pipeline.TryReadSelectedTextAsync(new ScreenPoint(123, 20), CancellationToken.None));
        Assert.True(reader.RequiresCompatibilityAt(new ScreenPoint(123, 20)));
        Assert.False(reader.RequiresCompatibilityAt(new ScreenPoint(456, 20)));
        Assert.Equal(1, clipboard.Calls);
        Assert.Null(await reader.TryReadSelectedTextAsync(new ScreenPoint(123, 20), CancellationToken.None));
        Assert.Equal(1, starts); // Avoid repeatedly crashing the same provider.
    }

    [Fact]
    public async Task ChildHangIsCancelledAndNextTargetCanUseAFreshWorker()
    {
        var starts = 0;
        using var reader = new IsolatedUiaSelectionReader(() => ++starts == 1
            ? Helper("$null = [Console]::ReadLine(); Start-Sleep -Seconds 30")
            : Helper("while ($null -ne [Console]::ReadLine()) { [Console]::WriteLine('{\"Capture\":{\"Text\":\"fresh\",\"Context\":null},\"Failed\":false}') }"), point => (uint)point.X);
        await reader.WarmUpAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.TryReadSelectedTextAsync(new ScreenPoint(1, 1), cancellation.Token));
        Assert.Equal("fresh", await reader.TryReadSelectedTextAsync(new ScreenPoint(2, 1), CancellationToken.None));
        Assert.Equal(2, starts);
    }

    [Fact]
    public async Task EmptySelectionDoesNotTurnOnClipboardFallback()
    {
        using var reader = new IsolatedUiaSelectionReader(() => Helper(
            "while ($null -ne [Console]::ReadLine()) { [Console]::WriteLine('{\"Capture\":null,\"Failed\":false}') }"), _ => 123);
        Assert.Null(await reader.TryReadSelectedTextAsync(new ScreenPoint(10, 20), CancellationToken.None));
        Assert.False(reader.RequiresCompatibilityAt(new ScreenPoint(10, 20)));
    }

    [Fact]
    public async Task PipelineTimeoutContinuesToClipboardInTheSameGesture()
    {
        using var reader = new IsolatedUiaSelectionReader(() => Helper(
            "$null = [Console]::ReadLine(); Start-Sleep -Seconds 30"), _ => 123);
        await reader.WarmUpAsync();
        var clipboard = new TextReader("After timeout");
        var pipeline = new SelectionReaderPipeline(reader, new TextReader(null), clipboard, () => false,
            allowTargetClipboardFallback: reader.RequiresCompatibilityAt);
        Assert.Equal("After timeout", await pipeline.TryReadSelectedTextAsync(new ScreenPoint(10, 20), CancellationToken.None));
        Assert.Equal(1, clipboard.Calls);
    }

    [Fact]
    public async Task UnicodeSelectionAndContextSurviveThePipe()
    {
        using var reader = new IsolatedUiaSelectionReader(() => Helper(
            "while ($null -ne [Console]::ReadLine()) { [Console]::WriteLine('{\"Capture\":{\"Text\":\"Hello 译獭 🦦\",\"Context\":\"段落 context\"},\"Failed\":false}') }"), _ => 123);
        var result = await reader.TryReadSelectionAsync(new ScreenPoint(10, 20), true, CancellationToken.None);
        Assert.Equal("Hello 译獭 🦦", result?.Text);
        Assert.Equal("段落 context", result?.Context);
    }

    [Fact]
    public async Task ProductionWorkerStartsWithoutMainAppAndUsesUtf8Protocol()
    {
        var info = IsolatedUiaSelectionReader.CreateStartInfo();
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardInput = info.RedirectStandardOutput = info.RedirectStandardError = true;
        info.StandardInputEncoding = info.StandardOutputEncoding = new UTF8Encoding(false);
        using var process = Process.Start(info)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            Assert.Equal("YITA-UIA-1", await process.StandardOutput.ReadLineAsync(timeout.Token));
            // EOF exits the helper without any UIA read or desktop interaction.
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }

    private sealed class TextReader(string? text) : ISelectionReader
    {
        internal int Calls;
        public Task<string?> TryReadSelectedTextAsync(ScreenPoint point, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(text); }
    }
}
