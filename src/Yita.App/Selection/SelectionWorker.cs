using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Yita.Models;

namespace Yita.Selection;

internal sealed record SelectionWorkerRequest(ScreenPoint Point, bool IncludeContext);
internal sealed record SelectionWorkerResponse(SelectionCapture? Capture, bool Failed = false);

internal static class SelectionWorker
{
    internal const string Ready = "YITA-UIA-1";

    internal static async Task<int> RunAsync(int parentId)
    {
        using Process parent = Process.GetProcessById(parentId);
        // Even a hung native call must not leave an orphan after the app exits.
        _ = Task.Run(async () => { await parent.WaitForExitAsync(); Environment.Exit(0); });
        using var input = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        var reader = new UiaSelectionReader(activateAccessibility: false);
        await output.WriteLineAsync(Ready);
        while (await input.ReadLineAsync() is { } line)
        {
            if (line.Length > 1024) return 2;
            var request = JsonSerializer.Deserialize<SelectionWorkerRequest>(line);
            if (request is null) return 2;
            SelectionWorkerResponse response;
            try
            {
                var capture = await reader.TryReadSelectionAsync(request.Point, request.IncludeContext, CancellationToken.None);
                response = new(capture);
            }
            catch (Exception e) when (e is not OutOfMemoryException and not AccessViolationException)
            {
                response = new(null, Failed: true);
            }
            await output.WriteLineAsync(JsonSerializer.Serialize(response));
        }
        return 0;
    }
}
