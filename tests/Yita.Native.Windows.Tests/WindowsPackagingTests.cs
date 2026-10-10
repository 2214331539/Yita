namespace Yita.Native.Windows.Tests;

public sealed class WindowsPackagingTests
{
    [Fact]
    public void WorkerDiscoveryPrefersIsolatedPublishedWorkerAndRetainsSourceAndOverridePaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "yita package " + Guid.NewGuid().ToString("N"));
        var packaged = Path.Combine(root, "Native", "WindowsUIA", "Yita.UIA.Worker.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(packaged)!);
        try
        {
            Assert.Null(WindowsUiAutomationWorkerClient.ResolveWorkerPath(root));
            var source = Path.Combine(root, "Yita.UIA.Worker.exe");
            File.WriteAllText(source, "fixture");
            Assert.Equal(source, WindowsUiAutomationWorkerClient.ResolveWorkerPath(root));
            File.WriteAllText(packaged, "fixture");
            Assert.Equal(packaged, WindowsUiAutomationWorkerClient.ResolveWorkerPath(root));
            Assert.Equal(source, WindowsUiAutomationWorkerClient.ResolveWorkerPath(root, source));
            Assert.Equal(packaged, WindowsUiAutomationWorkerClient.ResolveWorkerPath(root, Path.Combine(root, "missing")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
