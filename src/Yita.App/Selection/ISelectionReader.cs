using Yita.Models;

namespace Yita.Selection;

internal interface ISelectionReader
{
    Task<string?> TryReadSelectedTextAsync(ScreenPoint point, CancellationToken cancellationToken);
}

internal sealed record SelectionCapture(string Text, string? Context = null);

internal interface IContextualSelectionReader : ISelectionReader
{
    Task<SelectionCapture?> TryReadSelectionAsync(
        ScreenPoint point,
        bool includeContext,
        CancellationToken cancellationToken);
}
