using Yita.Selection;

namespace Yita;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // The helper must never initialize WPF, settings, credentials, hooks or
        // the single-instance coordinator. Native UIA faults stay in this process.
        if (args.Length == 2 && args[0] == "--selection-worker" && int.TryParse(args[1], out var parentId))
            return SelectionWorker.RunAsync(parentId).GetAwaiter().GetResult();

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
