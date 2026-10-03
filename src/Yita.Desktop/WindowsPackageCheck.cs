using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Yita.Native.Windows;

namespace Yita.Desktop;

internal static class WindowsPackageCheck
{
    internal static async Task<int> RunAsync()
    {
        try
        {
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                throw new PlatformNotSupportedException("The Windows package requires x64 Windows.");
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YITA_UIA_WORKER_PATH")))
                throw new InvalidOperationException("Package validation cannot use a worker override.");
            var worker = WindowsUiAutomationWorkerClient.ResolveWorkerPath(AppContext.BaseDirectory);
            var expected = Path.Combine(AppContext.BaseDirectory, "Native", "WindowsUIA", "Yita.UIA.Worker.exe");
            if (worker != expected) throw new InvalidOperationException("The isolated published UIA worker is missing.");
            var info = new ProcessStartInfo(worker)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            info.ArgumentList.Add("--package-check");
            using var process = Process.Start(info) ?? throw new IOException("Could not start the bundled worker.");
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var error = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                if (process.ExitCode != 0) throw new InvalidOperationException("The bundled worker could not load its runtime.");
                using var result = JsonDocument.Parse(await output);
                var root = result.RootElement;
                var version = typeof(Program).Assembly.GetName().Version?.ToString();
                if (root.GetProperty("status").GetString() != "passed"
                    || root.GetProperty("architecture").GetString() != "X64"
                    || root.GetProperty("version").GetString() != version
                    || root.GetProperty("automation").GetString() != "UIAutomationClient")
                    throw new InvalidOperationException("The bundled worker metadata does not match the desktop.");
                await error;
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    status = "passed", architecture = "x64", version,
                    runtime = RuntimeInformation.FrameworkDescription, workerRuntime = root.GetProperty("runtime").GetString(),
                    scope = "Self-contained desktop and UIA worker startup; no selection, settings, clipboard or API access."
                }));
                return 0;
            }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Windows package check failed: " + exception.GetType().Name + ": " + exception.Message);
            return 1;
        }
    }
}
