using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Yita.Core.Selection;
using Yita.Native.Mac;

namespace Yita.MacHelperSmoke;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--peer") return await RunPeerAsync(args[1]);
        if (args.Length != 0 && args is not ["--helper", _]) return 2;
        try
        {
            var starts = 0;
            ProcessStartInfo? Start()
            {
                starts++;
                if (args.Length == 0) return CreatePeerStartInfo("normal");
                var info = new ProcessStartInfo(args[1]);
                info.ArgumentList.Add("--self-test");
                return info;
            }
            using var client = new MacHelperClient(Start);
            using var adapter = new MacSelectionAdapter(client);
            var status = await adapter.GetStatusAsync();
            if (status.Service != NativeServiceState.Available)
                Console.Error.WriteLine($"Helper status: {status.Service}; diagnostic: {status.DiagnosticCode}");
            Check(status.Service == NativeServiceState.Available, "Versioned handshake and permission response accepted");
            Check(status.SelectionSupported == (args.Length != 0) && !status.GlobalInputSupported && !status.Permissions.CanAttemptSelection,
                "AX implementation, pending global input and denied self-test permissions are separate");
            var again = await adapter.GetStatusAsync();
            Check(again.Service == NativeServiceState.Available && starts == 1, "Multiple requests reuse one helper");
            var selection = await adapter.ReadAsync(new SelectionRequest(SelectionTrigger.TranslateShortcut, new ScreenPoint(10, 20)));
            Check(selection.Failure == (args.Length == 0 ? SelectionFailureKind.UnsupportedApplication : SelectionFailureKind.PermissionDenied),
                "Typed selection failures distinguish unsupported readers from denied permissions");
            var action = await client.SendAsync("requestAccessibility");
            Check(action.DiagnosticCode == "mac-helper-self-test-action-disabled", "Self-test never requests Accessibility authorization");
            if (args.Length != 0)
            {
                await VerifySwiftSelectionFixtureAsync(args[1]);
                await VerifySwiftClipboardFixtureAsync(args[1]);
            }
            Console.WriteLine("Mac helper protocol smoke passed. No desktop selection, GUI authorization, clipboard, Keychain or API access.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Mac helper smoke failed: " + exception.GetType().Name);
            return 1;
        }
    }

    private static async Task VerifySwiftSelectionFixtureAsync(string path)
    {
        ProcessStartInfo Start()
        {
            var info = new ProcessStartInfo(path);
            info.ArgumentList.Add("--self-test");
            info.ArgumentList.Add("--selection-fixture");
            info.ArgumentList.Add("range");
            return info;
        }
        using var client = new MacHelperClient(Start);
        using var adapter = new MacSelectionAdapter(client);
        var request = new SelectionRequest(SelectionTrigger.TranslateShortcut, new ScreenPoint(-700, 150),
            ForegroundApplication: "test.editor", ForegroundProcessId: 42);
        var selection = await adapter.ReadAsync(request);
        Check(selection.Succeeded && selection.Text == "hello world" && selection.Source == SelectionSource.Accessibility,
            "Swift range fixture round-trips a typed successful selection");
        Check(selection.Bounds == new SelectionBounds(-800, 120, 90, 18), "Quartz coordinates survive the Swift/C# exchange");
        Check(selection.Context is null, "Context stays absent without opt-in");
        var withContext = await adapter.ReadAsync(request with { IncludeContext = true });
        Check(withContext.Context == "prefix hello world suffix", "Explicit context requests round-trip a bounded snippet");
        var stale = await adapter.ReadAsync(request with { ForegroundProcessId = 43 });
        Check(stale.Failure == SelectionFailureKind.Cancelled && stale.Text is null
            && stale.DiagnosticCode == "mac-helper-ax-target-changed", "Changed target identities return typed cancellation without text");
    }

    private static async Task VerifySwiftClipboardFixtureAsync(string path)
    {
        var starts = 0;
        ProcessStartInfo Start()
        {
            starts++;
            var info = new ProcessStartInfo(path);
            info.ArgumentList.Add("--self-test");
            info.ArgumentList.Add("--selection-fixture");
            info.ArgumentList.Add("clipboard-slow");
            return info;
        }
        using var client = new MacHelperClient(Start);
        using var adapter = new MacSelectionAdapter(client);
        var request = new SelectionRequest(SelectionTrigger.TranslateShortcut, new ScreenPoint(-700, 150), ForegroundProcessId: 42);
        Check((await adapter.ReadAsync(request)).Failure == SelectionFailureKind.UnsupportedApplication,
            "Clipboard simulation is unavailable without explicit fallback opt-in");
        var selection = await adapter.ReadAsync(request, allowClipboardFallback: true);
        Check(selection.Succeeded && selection.Source == SelectionSource.ClipboardFallback && selection.Text == "copied selection",
            "Swift copy fixture restores every format before returning success");
        Check(selection.Context is null && selection.Bounds is null, "Copied text has no unrelated context or invented AX bounds");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        try
        {
            await adapter.ReadAsync(request, allowClipboardFallback: true, cancellationToken: cancel.Token);
            Check(false, "Clipboard cancellation is propagated");
        }
        catch (OperationCanceledException) { Check(true, "Clipboard cancellation finishes Swift cleanup before propagating"); }
        var recovered = await adapter.GetStatusAsync();
        if (recovered.Service != NativeServiceState.Available || starts != 1)
            Console.Error.WriteLine($"Cancellation recovery: {recovered.Service}; diagnostic: {recovered.DiagnosticCode}; helper starts: {starts}");
        Check(recovered.Service == NativeServiceState.Available && starts == 1,
            "The helper remains usable after canceled clipboard cleanup");
        using var closingClient = new MacHelperClient(Start);
        var permission = await closingClient.SendAsync("permissions");
        Check(permission.Response?.ProcessId is > 0, "Swift fixture exposes its helper PID for exit verification");
        using var child = Process.GetProcessById(permission.Response!.ProcessId!.Value);
        var pending = closingClient.SendAsync("readSelection", request, allowClipboardFallback: true);
        await Task.Delay(150);
        closingClient.Dispose();
        Check((await pending.WaitAsync(TimeSpan.FromSeconds(3))).State == NativeServiceState.Unavailable,
            "Disposal cancels pending clipboard work through stdin EOF");
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Check(child.ExitCode == 0, "Swift finishes fixture restoration and exits cleanly after EOF");
    }

    public static ProcessStartInfo CreatePeerStartInfo(string mode, string? runtimeConfig = null)
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(host))
        {
            var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
            host = Path.Combine(runtime.Parent!.Parent!.Parent!.FullName, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        }
        var info = new ProcessStartInfo(host);
        if (runtimeConfig is not null)
        {
            info.ArgumentList.Add("exec");
            info.ArgumentList.Add("--runtimeconfig");
            info.ArgumentList.Add(runtimeConfig);
        }
        info.ArgumentList.Add(typeof(Program).Assembly.Location);
        info.ArgumentList.Add("--peer");
        info.ArgumentList.Add(mode);
        return info;
    }

    private static async Task<int> RunPeerAsync(string mode)
    {
        if (mode == "no-handshake") { await Task.Delay(Timeout.Infinite); return 0; }
        if (mode == "stderr-flood")
        {
            await Console.OpenStandardError().WriteAsync(new byte[512_000]);
            await Console.OpenStandardError().FlushAsync();
        }
        await EmitAsync(new
        {
            version = mode == "wrong-version" ? MacHelperProtocol.Version - 1 : MacHelperProtocol.Version,
            id = "ready", status = "ready",
            bundleIdentifier = mode == "wrong-bundle" ? "unrelated.helper" : MacHelperProtocol.BundleIdentifier,
            processId = mode == "wrong-pid" ? Environment.ProcessId + 1 : Environment.ProcessId,
        });
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var id = document.RootElement.GetProperty("id").GetString();
            var command = document.RootElement.GetProperty("command").GetString();
            if (command == "cancelSelection") continue;
            if (command == "readSelection" && (mode is "copy-pending" or "copy-partial")
                && document.RootElement.GetProperty("allowClipboardFallback").GetBoolean())
            {
                var cancelled = JsonSerializer.Serialize(new MacHelperResponse
                {
                    Version = MacHelperProtocol.Version, Id = id!, Status = "ok",
                    Selection = SelectionResult.Failed(SelectionFailureKind.Cancelled, "selection-cancelled"),
                }, MacHelperProtocol.JsonOptions);
                var split = mode == "copy-partial" ? cancelled.Length / 2 : 0;
                if (split != 0) { await Console.Out.WriteAsync(cancelled[..split]); await Console.Out.FlushAsync(); }
                if (await Console.In.ReadLineAsync() is not { } controlLine) return 0;
                using var control = JsonDocument.Parse(controlLine);
                if (control.RootElement.GetProperty("command").GetString() != "cancelSelection"
                    || control.RootElement.GetProperty("id").GetString() != id) return 8;
                await Task.Delay(50);
                await Console.Out.WriteLineAsync(cancelled[split..]);
                await Console.Out.FlushAsync();
                continue;
            }
            if (mode == "hang") { await Task.Delay(Timeout.Infinite); return 0; }
            if (mode == "crash") return 7;
            if (mode == "oversize")
            {
                await Console.Out.WriteAsync(new string('x', MacHelperProtocol.MaximumResponseBytes + 1));
                await Console.Out.FlushAsync();
                await Task.Delay(Timeout.Infinite);
                return 0;
            }
            if (mode == "invalid-json") { await Console.Out.WriteLineAsync("{invalid"); await Console.Out.FlushAsync(); continue; }
            if (mode == "missing-permissions")
            { await EmitAsync(new { version = MacHelperProtocol.Version, id, status = "ok" }); continue; }
            if (mode == "private-error")
            { await EmitAsync(new { version = MacHelperProtocol.Version, id, status = "error", diagnosticCode = "private selected text" }); continue; }
            var response = new MacHelperResponse
            {
                Version = MacHelperProtocol.Version, Id = mode == "wrong-id" ? "old-response" : id!,
                Status = mode == "wrong-status" ? "unexpected" : "ok", ProcessId = Environment.ProcessId,
                Permissions = new MacHelperPermissions { Accessibility = mode == "selection", InputMonitoring = false, EventPosting = false },
                Capabilities = new MacHelperCapabilities { Selection = mode == "selection", ClipboardFallback = false, GlobalInput = false },
            };
            if (command == "readSelection") response = mode == "selection"
                ? response with { Selection = new SelectionResult("selected text", SelectionSource.Accessibility,
                    new SelectionBounds(10, 20, 100, 18), Context: "private context") }
                : response with { Status = "error", DiagnosticCode = "selection-not-implemented" };
            if (command is "requestAccessibility" or "openAccessibilitySettings")
                response = response with { Status = "error", DiagnosticCode = "self-test-action-disabled" };
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(response, MacHelperProtocol.JsonOptions));
            await Console.Out.FlushAsync();
        }
        return 0;
    }

    private static async Task EmitAsync<T>(T value)
    {
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(value));
        await Console.Out.FlushAsync();
    }
    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            Console.Error.WriteLine("FAIL: " + message);
            throw new InvalidOperationException();
        }
        Console.WriteLine("PASS: " + message);
    }
}
