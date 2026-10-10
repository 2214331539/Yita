using System.Diagnostics;
using System.Text.Json;
using Yita.Core.Platform;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args is ["--probe", var lockPath, var mode])
        {
            using var instance = new PortableSingleInstanceGuard(lockPath, requestActivation: mode == "activate");
            Console.WriteLine(JsonSerializer.Serialize(new ProbeResult(instance.IsOwner)));
            return 0;
        }
        if (args.Length != 0) return 2;

        var directory = Path.Combine(Path.GetTempPath(), "yita-platform-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "instance.lock");
            using var owner = new PortableSingleInstanceGuard(path);
            Check(owner.IsOwner, "Primary process acquires the lock");
            var activation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            owner.StartActivationListener(() => activation.TrySetResult());
            Check(!(await ProbeAsync(path, "background")).IsOwner, "Another process cannot acquire the same lock");
            Check(!activation.Task.IsCompleted, "Background launch does not open settings");
            Check(!(await ProbeAsync(path, "activate")).IsOwner, "Normal launch reuses the existing process");
            await activation.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(true, "Activation crosses the process boundary");
            owner.Dispose();
            Check((await ProbeAsync(path, "background")).IsOwner, "A new process acquires the lock after exit");
            Console.WriteLine("Platform smoke passed. No GUI permissions, API requests, product settings or clipboard changes.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Platform smoke failed: {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task<ProbeResult> ProbeAsync(string lockPath, string mode)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Process path unavailable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var argument in new[] { "--probe", lockPath, mode }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Probe did not start.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var stdout = await output;
            var stderr = await error;
            if (process.ExitCode != 0) throw new InvalidOperationException("Probe failed: " + stderr);
            return JsonSerializer.Deserialize<ProbeResult>(stdout) ?? throw new InvalidOperationException("Probe returned no status.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        Console.WriteLine("PASS: " + description);
    }

    private sealed record ProbeResult(bool IsOwner);
}
