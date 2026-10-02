using Yita.Core.Platform;
using Yita.Core.Selection;

namespace Yita.Native.Mac.Tests;

public sealed class MacInputRuntimeTests
{
    private static MacInputEvent Input(MacInputKind kind, long sequence, double x, int pid = 42,
        bool modified = false, double age = 0) => new()
    {
        Kind = kind, Sequence = sequence, Pointer = new(x, 150), AgeMilliseconds = age,
        ForegroundProcessId = pid, ForegroundApplication = "test.editor", Modified = modified,
    };
    private static MacInputSnapshot Batch(params MacInputEvent[] events) => new()
    { MouseRunning = true, HotkeyRunning = true, Sequence = events.LastOrDefault()?.Sequence ?? 0, Events = events };

    [Fact]
    public async Task ExternalDragCarriesSourceGenerationContextAndExplicitCopyPolicy()
    {
        using var helper = new TestHelper();
        using var runtime = new MacSelectionRuntime(helper);
        runtime.Configure(true, false, 0, useSelectionContext: true);
        var captured = new TaskCompletionSource<SelectionCapturedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.SelectionCaptured += (_, args) => captured.TrySetResult(args);
        await runtime.HandleInputAsync(Batch(Input(MacInputKind.PointerDown, 1, -800), Input(MacInputKind.PointerUp, 2, -700)));
        var selection = await captured.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(42, selection.Request.ForegroundProcessId);
        Assert.Equal("test.editor", selection.Request.ForegroundApplication);
        Assert.Equal(new SelectionBounds(-800, 150, 100, 0), selection.Request.GestureBounds);
        Assert.True(selection.Request.IncludeContext);
        Assert.False(helper.CopyAllowed);
        Assert.Equal(2, helper.Sequence);
        Assert.Equal(SelectionSource.Accessibility, selection.Result.Source);
    }

    [Fact]
    public async Task ClicksModifiersOwnWindowsAndSourceChangesDoNotReadText()
    {
        foreach (var scenario in new[] { "click", "modified", "own", "source" })
        {
            using var helper = new TestHelper();
            using var runtime = new MacSelectionRuntime(helper, (point, _) => Task.FromResult(scenario == "own"));
            runtime.Configure(true, true, 0);
            await runtime.HandleInputAsync(Batch(Input(MacInputKind.PointerDown, 1, -800, modified: scenario == "modified"),
                Input(MacInputKind.PointerUp, 2, scenario == "click" ? -799 : -700, pid: scenario == "source" ? 43 : 42)));
            Assert.Equal(0, helper.SelectionReads);
        }
    }

    [Fact]
    public async Task PausingKeepsExternalDismissalAndManualClipboardWithoutSendingCopy()
    {
        using var helper = new TestHelper();
        using var runtime = new MacSelectionRuntime(helper);
        var clicks = 0;
        runtime.ExternalPointerPressed += (_, _) => clicks++;
        runtime.Configure(false, true, 0);
        await runtime.HandleInputAsync(Batch(Input(MacInputKind.PointerDown, 1, -800), Input(MacInputKind.PointerUp, 2, -700)));
        Assert.Equal(1, clicks);
        Assert.Equal(0, helper.SelectionReads);
        var captured = new TaskCompletionSource<SelectionCapturedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.SelectionCaptured += (_, args) => captured.TrySetResult(args);
        await runtime.HandleInputAsync(Batch(Input(MacInputKind.TranslateClipboard, 3, -600)));
        var manual = await captured.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(SelectionSource.ManualClipboard, manual.Result.Source);
        Assert.Equal(SelectionTrigger.TranslateShortcut, manual.Request.Trigger);
        Assert.Equal(new ScreenPoint(-500, 200), manual.Request.Pointer);
        Assert.False(helper.CopyAllowed);
    }

    [Fact]
    public async Task StaleEventsAndQueueResetsCannotCompleteAPartialGesture()
    {
        using var helper = new TestHelper();
        using var runtime = new MacSelectionRuntime(helper);
        runtime.Configure(true, true, 0);
        await runtime.HandleInputAsync(Batch(Input(MacInputKind.PointerDown, 1, -800),
            Input(MacInputKind.Cancel, 2, 0), Input(MacInputKind.PointerUp, 3, -700)));
        await runtime.HandleInputAsync(Batch(Input(MacInputKind.PointerDown, 4, -800, age: 501), Input(MacInputKind.PointerUp, 5, -700)));
        Assert.Equal(0, helper.SelectionReads);
    }

    [Fact]
    public async Task InputThatExpiresDuringWindowFilteringCannotStartAGesture()
    {
        using var helper = new TestHelper();
        using var runtime = new MacSelectionRuntime(helper, async (_, _) => { await Task.Delay(30); return false; });
        runtime.Configure(true, true, 0);
        var clicks = 0;
        runtime.ExternalPointerPressed += (_, _) => clicks++;
        await runtime.HandleInputAsync(Batch(Input(MacInputKind.PointerDown, 1, -800, age: 490), Input(MacInputKind.PointerUp, 2, -700)));
        Assert.Equal(0, clicks);
        Assert.Equal(0, helper.SelectionReads);
    }

    [Fact]
    public async Task AHelperRestartDuringPermissionWorkReconfiguresInputInsteadOfLeavingItStopped()
    {
        using var helper = new TestHelper { LoseConfigurationOnFirstPoll = true };
        using var runtime = new MacSelectionRuntime(helper, pollInterval: TimeSpan.FromMilliseconds(5));
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.StatusChanged += (_, _) =>
        {
            if (runtime.IsRunning && helper.Commands.Count(command => command == "configureInput") >= 2) running.TrySetResult();
        };
        runtime.Start();
        await helper.Reconfigured.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await running.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(helper.Commands.Count(command => command == "configureInput") >= 2);
        Assert.True(runtime.IsRunning);
    }

    [Fact]
    public async Task LaterInputAndPauseDiscardReaderResultsEvenWhenCancellationIsIgnored()
    {
        foreach (var pause in new[] { false, true })
        {
            using var helper = new TestHelper { DelayedSelection = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            using var runtime = new MacSelectionRuntime(helper);
            runtime.Configure(true, true, 0);
            var deliveries = 0;
            runtime.SelectionCaptured += (_, _) => deliveries++;
            await runtime.HandleInputAsync(Batch(Input(MacInputKind.PointerDown, 1, -800), Input(MacInputKind.PointerUp, 2, -700)));
            Assert.Equal(1, helper.SelectionReads);
            if (pause) runtime.Configure(false, true, 0);
            else await runtime.HandleInputAsync(Batch(Input(MacInputKind.PointerDown, 3, -400)));
            helper.DelayedSelection.SetResult(Success("readSelection"));
            await Task.Delay(30);
            Assert.Equal(0, deliveries);
            Assert.True(helper.SelectionToken.IsCancellationRequested);
        }
    }

    [Fact]
    public async Task DuplicateBatchesCannotRepeatATranslationAndSubscribersCannotStopInput()
    {
        using var helper = new TestHelper();
        using var runtime = new MacSelectionRuntime(helper);
        runtime.Configure(true, true, 0);
        runtime.ExternalPointerPressed += (_, _) => throw new InvalidOperationException();
        var batch = Batch(Input(MacInputKind.PointerDown, 1, -800), Input(MacInputKind.PointerUp, 2, -700));
        await runtime.HandleInputAsync(batch);
        await runtime.HandleInputAsync(batch);
        Assert.Equal(1, helper.SelectionReads);
    }

    [Fact]
    public async Task SuspensionCancelsPendingTextAndBlocksManualClipboardUntilResume()
    {
        using var helper = new TestHelper { DelayedSelection = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var runtime = new MacSelectionRuntime(helper);
        runtime.Configure(true, true, 0);
        var deliveries = 0;
        var activity = new List<bool>();
        runtime.SelectionCaptured += (_, _) => deliveries++;
        runtime.SessionActivityChanged += (_, active) => activity.Add(active);
        await runtime.HandleInputAsync(Batch(Input(MacInputKind.PointerDown, 1, -800), Input(MacInputKind.PointerUp, 2, -700)));
        await runtime.HandleInputAsync(Batch() with { Sequence = 3, SessionActive = false, SessionGeneration = 1 });
        runtime.TranslateClipboard();
        Assert.DoesNotContain("readClipboard", helper.Commands);
        Assert.True(helper.SelectionToken.IsCancellationRequested);
        helper.DelayedSelection.SetResult(Success("readSelection"));
        await Task.Delay(30);
        Assert.Equal(0, deliveries);
        await runtime.HandleInputAsync(Batch() with { Sequence = 4, SessionGeneration = 2 });
        var manual = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.SelectionCaptured += (_, _) => manual.TrySetResult();
        runtime.TranslateClipboard();
        await manual.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new[] { false, true }, activity);
        Assert.True(runtime.IsSessionActive);
    }

    [Fact]
    public async Task AMissedSleepWakePairStillResetsAPartialGesture()
    {
        using var helper = new TestHelper();
        using var runtime = new MacSelectionRuntime(helper);
        runtime.Configure(true, true, 0);
        var activity = new List<bool>();
        runtime.SessionActivityChanged += (_, active) => activity.Add(active);
        await runtime.HandleInputAsync(Batch(Input(MacInputKind.PointerDown, 1, -800)));
        await runtime.HandleInputAsync(Batch(Input(MacInputKind.PointerUp, 3, -700)) with { SessionGeneration = 2 });
        Assert.Equal(0, helper.SelectionReads);
        Assert.Equal(new[] { false, true }, activity);
        await runtime.HandleInputAsync(Batch() with { SessionGeneration = 2 });
        Assert.Equal(2, activity.Count);
    }

    [Theory]
    [InlineData("too-many")]
    [InlineData("unordered")]
    [InlineData("stale")]
    [InlineData("missing-source")]
    [InlineData("invalid-point")]
    [InlineData("suspended-input")]
    [InlineData("negative-session")]
    public void InputProtocolRejectsMalformedOrUnboundedBatches(string mode)
    {
        var item = Input(MacInputKind.PointerDown, 1, 0);
        var events = mode switch
        {
            "too-many" => Enumerable.Range(1, 65).Select(i => item with { Sequence = i }).ToArray(),
            "unordered" => new[] { item, item },
            "stale" => new[] { item with { AgeMilliseconds = 501 } },
            "missing-source" => new[] { item with { ForegroundProcessId = null } },
            "negative-session" or "suspended-input" => new[] { item },
            _ => new[] { item with { Pointer = new(double.NaN, 0) } },
        };
        var batch = mode == "suspended-input" ? Batch(events) with { SessionActive = false }
            : mode == "negative-session" ? Batch(events) with { SessionGeneration = -1 } : Batch(events);
        var response = Success("permissions").Response! with { Input = batch };
        Assert.Throws<MacHelperProtocolException>(() => MacHelperProtocol.ValidateResponse(response, "test", "pollInput"));
    }

    [Fact]
    public async Task RuntimeSharesItsPermissionHelperAndStartsPollingWithoutRequestingAuthorization()
    {
        using var helper = new TestHelper();
        using var runtime = new MacSelectionRuntime(helper, pollInterval: TimeSpan.FromMilliseconds(5));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.StatusChanged += (_, _) => { if (runtime.IsRunning) ready.TrySetResult(); };
        runtime.Start();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(runtime.IsHotkeyRunning);
        Assert.True((await runtime.GetStatusAsync()).GlobalInputSupported);
        Assert.DoesNotContain("requestAccessibility", helper.Commands);
        Assert.DoesNotContain("requestInputMonitoring", helper.Commands);
        runtime.Dispose();
        Assert.True(helper.Disposed);
        Assert.False(runtime.IsRunning);
    }

    private static MacHelperExchange Success(string command) => new(NativeServiceState.Available, new MacHelperResponse
    {
        Version = MacHelperProtocol.Version, Id = "test", Status = "ok",
        Permissions = new() { Accessibility = true, InputMonitoring = true, EventPosting = true },
        Capabilities = new() { Selection = true, GlobalInput = true, ClipboardFallback = true },
        Pointer = new(-500, 200),
        Input = Batch(),
        Selection = new(command == "readClipboard" ? "manual" : "selection",
            command == "readClipboard" ? SelectionSource.ManualClipboard : SelectionSource.Accessibility),
    });

    private sealed class TestHelper : IMacHelperClient
    {
        public int SelectionReads { get; private set; }
        public bool CopyAllowed { get; private set; }
        public long? Sequence { get; private set; }
        public bool Disposed { get; private set; }
        public CancellationToken SelectionToken { get; private set; }
        public TaskCompletionSource<MacHelperExchange>? DelayedSelection { get; init; }
        public bool LoseConfigurationOnFirstPoll { get; init; }
        private bool _lost;
        private int _configurations;
        public TaskCompletionSource Reconfigured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public System.Collections.Concurrent.ConcurrentBag<string> Commands { get; } = new();
        public Task<MacHelperExchange> SendAsync(string command, SelectionRequest? selection = null,
            CancellationToken cancellationToken = default, bool allowClipboardFallback = false, MacInputOptions? input = null)
        {
            Commands.Add(command);
            if (command == "configureInput" && ++_configurations == 2) Reconfigured.TrySetResult();
            if (command == "pollInput" && LoseConfigurationOnFirstPoll && !_lost)
            {
                _lost = true;
                return Task.FromResult(Success(command) with { Response = Success(command).Response! with
                    { DiagnosticCode = "input-not-configured", Input = Batch() with { MouseRunning = false, HotkeyRunning = false } } });
            }
            if (command == "readSelection")
            {
                SelectionReads++; CopyAllowed = allowClipboardFallback; Sequence = input?.Sequence; SelectionToken = cancellationToken;
                if (DelayedSelection is { } delayed) return delayed.Task;
            }
            return Task.FromResult(Success(command));
        }
        public void Dispose() => Disposed = true;
    }
}
