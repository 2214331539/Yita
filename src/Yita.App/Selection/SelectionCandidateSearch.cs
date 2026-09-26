using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace Yita.Selection;

internal enum SelectionNodeAccess
{
    Readable,
    Protected,
    Unavailable,
}

/// <summary>
/// Validates a complete ancestor path before reading it. A detached hit node
/// invalidates only that path; a confirmed password ancestor stops all reads.
/// Nodes and selections are never retained across gestures or layout changes.
/// </summary>
internal static class SelectionCandidateSearch
{
    internal static SelectionCapture? Read<T>(
        T? hit,
        Func<T?> getFocused,
        Func<T, SelectionNodeAccess> getAccess,
        Func<T, T?> getParent,
        Func<T, SelectionCapture?> read,
        Func<IEnumerable<T>> getDocuments,
        CancellationToken cancellationToken) where T : class
    {
        var hitPath = ValidatePath(hit, getAccess, getParent, cancellationToken);
        if (hitPath.Access == SelectionNodeAccess.Protected)
        {
            return null;
        }

        foreach (var node in hitPath.Nodes)
        {
            if (TryRead(node, read, cancellationToken) is { } capture)
            {
                return capture;
            }
        }

        var focusedPath = ValidatePath(getFocused(), getAccess, getParent, cancellationToken);
        if (focusedPath.Access == SelectionNodeAccess.Protected)
        {
            return null;
        }

        foreach (var node in focusedPath.Nodes)
        {
            if (TryRead(node, read, cancellationToken) is { } capture)
            {
                return capture;
            }
        }

        foreach (var document in getDocuments())
        {
            var path = ValidatePath(document, getAccess, getParent, cancellationToken);
            if (path.Access == SelectionNodeAccess.Readable
                && TryRead(document, read, cancellationToken) is { } capture)
            {
                return capture;
            }
        }

        return null;
    }

    private static (SelectionNodeAccess Access, IReadOnlyList<T> Nodes) ValidatePath<T>(
        T? node,
        Func<T, SelectionNodeAccess> getAccess,
        Func<T, T?> getParent,
        CancellationToken cancellationToken) where T : class
    {
        var path = new List<T>();
        try
        {
            while (node is not null && path.Count < UiaSelectionReader.MaximumAncestorDepth)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var access = getAccess(node);
                if (access != SelectionNodeAccess.Readable)
                {
                    return (access, Array.Empty<T>());
                }

                path.Add(node);
                node = getParent(node);
            }

            // Do not read below an ancestor whose safety we could not establish.
            return node is null
                ? (SelectionNodeAccess.Readable, path)
                : (SelectionNodeAccess.Unavailable, Array.Empty<T>());
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return (SelectionNodeAccess.Unavailable, Array.Empty<T>());
        }
    }

    private static SelectionCapture? TryRead<T>(
        T node,
        Func<T, SelectionCapture?> read,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var capture = read(node);
            return string.IsNullOrWhiteSpace(capture?.Text) ? null : capture;
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return null;
        }
    }

    internal static bool IsUnavailable(Exception exception) =>
        exception is ElementNotAvailableException or InvalidOperationException or COMException;
}
