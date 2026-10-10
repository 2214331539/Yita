using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Yita.Core.Selection;
using Yita.Native.Mac;

namespace Yita.Desktop;

internal static class MacPackageCheck
{
    internal static async Task<int> RunAsync()
    {
        try
        {
            if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
                throw new PlatformNotSupportedException("The preview package requires Apple Silicon macOS.");
            if (!new MacStartupRegistration().IsSupported)
                throw new InvalidOperationException("The executable must be inside a valid Yita.app bundle.");
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YITA_MAC_HELPER_PATH")))
                throw new InvalidOperationException("Package validation cannot use a helper override.");
            var helper = MacHelperClient.ResolveHelperPath(AppContext.BaseDirectory);
            using var client = new MacHelperClient(() =>
            {
                var info = new ProcessStartInfo(helper);
                info.ArgumentList.Add("--self-test");
                return info;
            });
            var response = await client.SendAsync("permissions");
            if (response.State != NativeServiceState.Available || response.Response?.Status != "ok")
                throw new InvalidOperationException("The bundled helper did not complete its protocol self-test.");
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                status = "passed", architecture = "arm64", runtime = RuntimeInformation.FrameworkDescription,
                version = typeof(Program).Assembly.GetName().Version?.ToString(),
                bundle = "com.yita.desktop", helper = "com.yita.desktop.native-helper",
                scope = "Bundle metadata, self-contained runtime and fixture helper communication; no desktop authorization or selection."
            }));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Mac package check failed: " + exception.GetType().Name + ": " + exception.Message);
            return 1;
        }
    }
}
