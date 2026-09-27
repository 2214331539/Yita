using System.Runtime.Versioning;
using Yita.Core.Selection;

namespace Yita.Native.Windows;

/// <summary>
/// Windows selection boundary. The existing WPF UIA implementation remains
/// the production reader during the migration; this adapter is the stable
/// contract that the Avalonia shell will consume in the next phase.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSelectionAdapter : ISelectionReader
{
    private readonly Func<SelectionRequest, CancellationToken, Task<SelectionResult>> _reader;

    public WindowsSelectionAdapter(
        Func<SelectionRequest, CancellationToken, Task<SelectionResult>> reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public Task<SelectionResult> ReadAsync(
        SelectionRequest request,
        CancellationToken cancellationToken = default) =>
        _reader(request, cancellationToken);
}

public interface IWindowsHotkeyService
{
    event EventHandler? TranslateRequested;

    void Start();

    void Stop();
}
