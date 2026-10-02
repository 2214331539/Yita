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
            Check(!status.SelectionSupported && !status.GlobalInputSupported && !status.Permissions.CanAttemptSelection,
                "Unsupported selection/input and self-test permissions are reported honestly");
            var again = await adapter.GetStatusAsync();
            Check(again.Service == NativeServiceState.Available && starts == 1, "Multiple requests reuse one helper");
            var selection = await adapter.ReadAsync(new SelectionRequest(SelectionTrigger.TranslateShortcut, new ScreenPoint(10, 20)));
            Check(selection.Failure == SelectionFailureKind.UnsupportedApplication, "Unimplemented selection is distinct from denied permissions");
            var action = await client.SendAsync("requestAccessibility");
            Check(action.DiagnosticCode == "mac-helper-self-test-action-disabled", "Self-test never requests Accessibility authorization");
            Console.WriteLine("Mac helper protocol smoke passed. No desktop selection, GUI authorization, clipboard, Keychain or API access.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Mac helper smoke failed: " + exception.GetType().Name);
            return 1;
        }
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
            version = mode == "wrong-version" ? 2 : MacHelperProtocol.Version,
            id = "ready", status = "ready",
            bundleIdentifier = mode == "wrong-bundle" ? "unrelated.helper" : MacHelperProtocol.BundleIdentifier,
            processId = mode == "wrong-pid" ? Environment.ProcessId + 1 : Environment.ProcessId,
        });
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var id = document.RootElement.GetProperty("id").GetString();
            var command = document.RootElement.GetProperty("command").GetString();
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
            { await EmitAsync(new { version = 1, id, status = "ok" }); continue; }
            if (mode == "private-error")
            { await EmitAsync(new { version = 1, id, status = "error", diagnosticCode = "private selected text" }); continue; }
            var response = new MacHelperResponse
            {
                Version = 1, Id = mode == "wrong-id" ? "old-response" : id!,
                Status = mode == "wrong-status" ? "unexpected" : "ok", ProcessId = Environment.ProcessId,
                Permissions = new MacHelperPermissions { Accessibility = mode == "selection", InputMonitoring = false },
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
