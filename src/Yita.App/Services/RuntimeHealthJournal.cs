using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace Yita.Services;

internal enum RuntimeHealthEvent
{
    AppStarted,
    SecondaryInstanceExited,
    PrimaryInstanceUnresponsive,
    StartupFailed,
    MouseHookStarted,
    MouseHookRecoverySucceeded,
    MouseHookRecoveryFailed,
    MouseHookStoppedUnexpectedly,
    HotkeyStarted,
    HotkeyRegistrationFailed,
    HotkeyStoppedUnexpectedly,
    HotkeyRecovered,
    ZoteroBridgeStarted,
    ZoteroBridgeStartFailed,
    SettingsReadFailed,
    CredentialReadFailed,
    CredentialRecovered,
    ShutdownRequested,
    DispatcherUnhandledException,
    ProcessUnhandledException,
    UnobservedTaskException,
    PlacementSaveFailed,
    PopupRenderFailed,
    WpsCopyEmpty,
    WpsCopySucceeded,
    SelectionWorkerFailed,
    ServiceDisposeFailed,
    SettingsLifecycleCheck,
    PlainWindowLifecycleCheck,
    PlainWindowSecondLifecycleCheck,
    SettingsLifecycleGdiCheck,
    SettingsLifecycleUserCheck,
    AppExited,
}

/// <summary>
/// A small privacy-safe lifecycle journal for intermittent failures. It accepts
/// only fixed event names, numeric codes, exception types, HRESULTs and bounded
/// application method names; exception messages, source paths, selected text, translations, endpoints,
/// paths and credentials are deliberately never written.
/// </summary>
internal sealed class RuntimeHealthJournal
{
    internal const long MaximumFileBytes = 64 * 1024;
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private readonly object _syncRoot = new();
    private readonly string _journalPath;

    public RuntimeHealthJournal(string? journalPath = null)
    {
        _journalPath = journalPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Yita",
            "runtime-health.log");
    }

    internal string JournalPath => _journalPath;

    public void Record(
        RuntimeHealthEvent healthEvent,
        Exception? exception = null,
        int? numericCode = null)
    {
        try
        {
            var line = BuildLine(healthEvent, exception, numericCode);
            lock (_syncRoot)
            {
                var directory = Path.GetDirectoryName(_journalPath);
                if (string.IsNullOrWhiteSpace(directory))
                {
                    return;
                }

                Directory.CreateDirectory(directory);
                RotateIfNeeded(line.Length);
                File.AppendAllText(_journalPath, line + Environment.NewLine, Utf8WithoutBom);
            }
        }
        catch (Exception journalException) when (!IsFatal(journalException))
        {
            // Diagnostics must never affect application availability.
        }
    }

    public string CreateReport(bool useChinese, int maximumEvents = 20)
    {
        var events = ReadRecent(maximumEvents);
        var lines = new List<string>
        {
            useChinese ? "运行健康记录" : "Runtime health journal",
        };
        if (events.Count == 0)
        {
            lines.Add(useChinese ? "尚无记录。" : "No events recorded yet.");
        }
        else
        {
            lines.AddRange(events);
        }

        lines.Add(useChinese
            ? "仅含生命周期事件、异常类型、代码方法名和数字状态；不含原文、译文、凭据、Endpoint 或文件路径。"
            : "Contains lifecycle events, exception types, application method names and numeric status; no selected text, translations, credentials, endpoint, or file path.");
        return string.Join(Environment.NewLine, lines);
    }

    private IReadOnlyList<string> ReadRecent(int maximumEvents)
    {
        if (maximumEvents <= 0)
        {
            return Array.Empty<string>();
        }

        try
        {
            lock (_syncRoot)
            {
                return File.Exists(_journalPath)
                    ? File.ReadLines(_journalPath, Utf8WithoutBom).TakeLast(maximumEvents).ToArray()
                    : Array.Empty<string>();
            }
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return Array.Empty<string>();
        }
    }

    private static string BuildLine(
        RuntimeHealthEvent healthEvent,
        Exception? exception,
        int? numericCode)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var builder = new StringBuilder(192)
            .Append(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))
            .Append(" event=")
            .Append(healthEvent)
            .Append(" version=")
            .Append(version?.ToString(3) ?? "0.0.0")
            .Append(" pid=")
            .Append(Environment.ProcessId.ToString(CultureInfo.InvariantCulture))
            .Append(" session=")
            .Append(System.Diagnostics.Process.GetCurrentProcess().SessionId.ToString(CultureInfo.InvariantCulture));
        if (numericCode is { } code)
        {
            builder.Append(" code=").Append(code.ToString(CultureInfo.InvariantCulture));
        }

        if (exception is not null)
        {
            builder
                .Append(" exception=")
                .Append(exception.GetType().FullName ?? exception.GetType().Name)
                .Append(" hresult=")
                .Append(exception.HResult.ToString(CultureInfo.InvariantCulture));
            AppendFrames(builder, exception);
            var inner = exception.InnerException;
            for (var depth = 0; inner is not null && depth < 3; depth++, inner = inner.InnerException)
            {
                builder.Append(" inner=").Append(inner.GetType().FullName)
                    .Append(":").Append(inner.HResult.ToString(CultureInfo.InvariantCulture));
                AppendFrames(builder, inner);
            }
        }

        return builder.ToString();
    }

    private static void AppendFrames(StringBuilder builder, Exception exception)
    {
        var frames = new System.Diagnostics.StackTrace(exception, false).GetFrames();
        foreach (var method in frames.Select(frame => frame.GetMethod())
                     .Where(method => method?.DeclaringType?.Assembly == typeof(RuntimeHealthJournal).Assembly)
                     .Take(8))
        {
            builder.Append(" at=").Append(method!.DeclaringType!.FullName).Append('.').Append(method.Name);
        }
    }

    private void RotateIfNeeded(int incomingCharacterCount)
    {
        if (!File.Exists(_journalPath))
        {
            return;
        }

        var estimatedIncomingBytes = Utf8WithoutBom.GetMaxByteCount(incomingCharacterCount + Environment.NewLine.Length);
        var currentLength = new FileInfo(_journalPath).Length;
        if (currentLength + estimatedIncomingBytes <= MaximumFileBytes)
        {
            return;
        }

        var previousPath = _journalPath + ".previous";
        File.Move(_journalPath, previousPath, overwrite: true);
    }

    private static bool IsFatal(Exception exception)
    {
        return exception is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or AppDomainUnloadedException
            or BadImageFormatException;
    }
}
