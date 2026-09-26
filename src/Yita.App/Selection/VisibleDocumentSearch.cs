namespace Yita.Selection;

internal readonly record struct DocumentNodeInfo(
    bool IsDocument,
    bool IsOffscreen,
    bool ContainsPointer,
    bool IsPassword);

/// <summary>
/// Searches live visible branches nearest the gesture first. The bounds apply
/// to traversal work, not to the first N pages in the PDF's document order.
/// </summary>
internal static class VisibleDocumentSearch
{
    internal const int MaximumNodes = 256;

    internal static IEnumerable<T> Find<T>(
        T root,
        Func<T, DocumentNodeInfo?> getInfo,
        Func<T, IEnumerable<T>> getChildren,
        CancellationToken cancellationToken) where T : class
    {
        var pending = new PriorityQueue<(T Node, int Depth, DocumentNodeInfo Info), (int Rank, int Order)>();
        var examined = 0;

        void Enqueue(T node, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++examined > MaximumNodes || depth >= UiaSelectionReader.MaximumAncestorDepth)
            {
                return;
            }

            DocumentNodeInfo? info;
            try
            {
                info = getInfo(node);
            }
            catch (Exception exception) when (SelectionCandidateSearch.IsUnavailable(exception))
            {
                return;
            }

            if (info is not { IsPassword: false } readable)
            {
                return;
            }

            var rank = readable.ContainsPointer ? 0 : readable.IsOffscreen ? 2 : 1;
            pending.Enqueue((node, depth, readable), (rank, examined));
        }

        Enqueue(root, 0);
        while (pending.TryDequeue(out var item, out _))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Info.IsDocument && (!item.Info.IsOffscreen || item.Info.ContainsPointer))
            {
                yield return item.Node;
            }

            // Offscreen branches are not expanded; visible containers that lack
            // their own TextPattern can still lead to the current PDF document.
            if (item.Info.IsOffscreen && !item.Info.ContainsPointer || examined >= MaximumNodes)
            {
                continue;
            }

            using var children = getChildren(item.Node).GetEnumerator();
            while (examined < MaximumNodes)
            {
                T child;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!children.MoveNext())
                    {
                        break;
                    }

                    child = children.Current;
                }
                catch (Exception exception) when (SelectionCandidateSearch.IsUnavailable(exception))
                {
                    break;
                }

                Enqueue(child, item.Depth + 1);
            }
        }
    }
}
