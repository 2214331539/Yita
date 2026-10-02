using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Yita.Core.Selection;
using Yita.Native.Windows;

internal static class Program
{
    private const string Selected = "Selected text from a native Windows editor.";
    private const string Sentinel = "Yita smoke test clipboard";
    private static IntPtr _clipboardWindow;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "--fixture") return RunFixture();
        using var original = WindowsClipboardSnapshot.TryCapture();
        if (original is null) { Console.Error.WriteLine("Clipboard cannot be preserved; no smoke test performed."); return 2; }
        using var owner = new Form();
        _clipboardWindow = owner.Handle;
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Process path unavailable.");
        var start = new ProcessStartInfo(executable, "--fixture") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.Arguments = '"' + typeof(Program).Assembly.Location + "\" --fixture";
        using var child = Process.Start(start)!;
        try
        {
            var line = child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
            var target = JsonSerializer.Deserialize<Target>(line!)!;
            var request = new SelectionRequest(SelectionTrigger.MouseGesture, new ScreenPoint(target.X, target.Y));
            FocusFixture(new IntPtr(target.Window));
            Thread.Sleep(200);
            Environment.SetEnvironmentVariable("YITA_UIA_WORKER_PATH", Path.GetFullPath("src/Yita.Desktop/bin/Release/net8.0/Yita.UIA.Worker.exe"));
            using (var reader = new WindowsUiAutomationSelectionReader())
            {
                var result = reader.ReadAsync(request).GetAwaiter().GetResult();
                Console.WriteLine($"Worker result: {result.Source}, {result.Failure}, {result.DiagnosticCode}, {result.Text?.Length ?? 0} characters.");
                Check(result.Succeeded && result.Text == Selected, "UI Automation worker captures a real native editor selection");
                Check(result.Bounds is { Width: > 0, Height: > 0 }, "UI Automation returns selection coordinates");
            }
            var readerFallback = new WindowsClipboardSelectionReader();
            WriteSentinel();
            FocusFixture(new IntPtr(target.Window));
            Thread.Sleep(100);
            var copied = readerFallback.ReadAsync(request).GetAwaiter().GetResult();
            Console.WriteLine($"Copy result: {copied.Failure}, {copied.DiagnosticCode}, {copied.Text?.Length ?? 0} characters.");
            Check(copied.Succeeded && copied.Text == Selected, "Ctrl+C fallback captures selected text");
            CheckSentinel("Normal fallback restores text and HTML clipboard formats");
            WriteSentinel();
            FocusFixture(new IntPtr(target.Window));
            Thread.Sleep(100);
            // The fixture deliberately takes 150 ms to service copy. Cancel
            // after delivery, before the target owns the copied clipboard.
            using var cancel = new CancellationTokenSource(40);
            try { readerFallback.ReadAsync(request, cancel.Token).GetAwaiter().GetResult(); throw new Exception("Expected cancellation."); }
            catch (OperationCanceledException) { }
            CheckSentinel("Cancelled fallback still restores every preserved format");
            using (var tray = new WindowsTrayIcon(Path.GetFullPath("src/Yita.Desktop/Assets/Yita.ico")))
                Check(tray.IsRunning, "Native tray icon starts its message loop");
            using (var runtime = new WindowsSelectionRuntime())
            {
                runtime.Start();
                Check(runtime.IsRunning, "Mouse hook starts on the Windows desktop");
                Check(runtime.IsHotkeyRunning, "Global Ctrl+Shift+T registers");
                runtime.RepairInputCapture();
                Check(runtime.IsRunning, "Manual mouse hook repair succeeds");
            }
            Console.WriteLine("Windows native smoke checks passed. No API requests or product settings writes.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception.Message); return 1; }
        finally
        {
            if (!child.HasExited) { PostMessage(new IntPtr(child.MainWindowHandle), 0x10, IntPtr.Zero, IntPtr.Zero); if (!child.WaitForExit(2000)) child.Kill(); }
            var sequence = GetClipboardSequenceNumber();
            var ownerWindow = GetClipboardOwner();
            for (var attempt = 0; attempt < 10; attempt++)
            {
                if (original.TryRestore(sequence, ownerWindow)) break;
                Thread.Sleep(20);
            }
        }
    }
    private static int RunFixture()
    {
        var application = new System.Windows.Application();
        var form = new System.Windows.Window { Title = "Yita native test fixture", Width = 620, Height = 220, Left = 80, Top = 80, Topmost = true };
        var editor = new System.Windows.Controls.TextBox { AcceptsReturn = true, FontSize = 14, Text = Selected + "\nUnselected surrounding paragraph." };
        form.Content = editor;
        editor.CommandBindings.Add(new System.Windows.Input.CommandBinding(System.Windows.Input.ApplicationCommands.Copy,
            (_, _) => { Thread.Sleep(150); System.Windows.Clipboard.SetText(editor.SelectedText); }, (_, e) => e.CanExecute = true));
        form.ContentRendered += (_, _) =>
        {
            editor.Focus(); editor.Select(0, Selected.Length);
            var point = editor.PointToScreen(new System.Windows.Point(25, 20));
            var handle = new System.Windows.Interop.WindowInteropHelper(form).Handle;
            Console.WriteLine(JsonSerializer.Serialize(new Target(handle.ToInt64(), (int)point.X, (int)point.Y)));
            Console.Out.Flush();
        };
        application.Run(form);
        return 0;
    }
    private static void WriteSentinel()
    {
        if (!OpenClipboard(_clipboardWindow)) throw new Exception("Cannot open fixture clipboard.");
        try
        {
            if (!EmptyClipboard()) throw new Exception("Cannot initialize fixture clipboard.");
            SetBytes(13, Encoding.Unicode.GetBytes(Sentinel + "\0"));
            SetBytes(RegisterClipboardFormat("HTML Format"), Encoding.UTF8.GetBytes("<b>Yita</b>\0"));
        }
        finally { CloseClipboard(); }
    }
    private static void CheckSentinel(string description)
    {
        if (!OpenClipboard(IntPtr.Zero)) throw new Exception("Clipboard remained locked.");
        try
        {
            Check(ReadFormat(13, Encoding.Unicode) == Sentinel && ReadFormat(RegisterClipboardFormat("HTML Format"), Encoding.UTF8) == "<b>Yita</b>", description);
        }
        finally { CloseClipboard(); }
    }
    private static void SetBytes(uint format, byte[] bytes)
    {
        var handle = GlobalAlloc(2, (UIntPtr)bytes.Length);
        var pointer = GlobalLock(handle);
        Marshal.Copy(bytes, 0, pointer, bytes.Length); GlobalUnlock(handle);
        if (SetClipboardData(format, handle) == IntPtr.Zero) { GlobalFree(handle); throw new Exception("Cannot write fixture clipboard format."); }
    }
    private static string ReadFormat(uint format, Encoding encoding)
    {
        var handle = GetClipboardData(format);
        if (handle == IntPtr.Zero) return "";
        var bytes = new byte[(int)GlobalSize(handle)]; var pointer = GlobalLock(handle);
        try { Marshal.Copy(pointer, bytes, 0, bytes.Length); return encoding.GetString(bytes).TrimEnd('\0'); }
        finally { GlobalUnlock(handle); }
    }
    private static void Check(bool condition, string description)
    { if (!condition) throw new InvalidOperationException("FAIL: " + description); Console.WriteLine("PASS: " + description); }
    private sealed record Target(long Window, int X, int Y);
    private static void FocusFixture(IntPtr target)
    {
        SetForegroundWindow(target);
        if (Environment.GetEnvironmentVariable("YITA_INTERACTIVE_SMOKE") == "1")
        {
            Console.WriteLine("Activate the Yita native test fixture title bar to continue input verification.");
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (WindowsNativeMethods.GetForegroundWindow() != target && DateTime.UtcNow < deadline) Thread.Sleep(100);
        }
        if (WindowsNativeMethods.GetForegroundWindow() != target)
            throw new InvalidOperationException("Windows did not grant focus to the test fixture; input verification cannot continue.");
    }
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr window);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr size);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr memory);
}
