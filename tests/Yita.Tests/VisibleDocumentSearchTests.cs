using System.Windows.Automation;
using Yita.Selection;

namespace Yita.Tests;

public sealed class VisibleDocumentSearchTests
{
    [Fact]
    public void FindsVisiblePdfPageBeyondFirstEightDocumentNodes()
    {
        var current = new Node { Info = new(true, false, true, false) };
        var earlierPages = Enumerable.Range(0, 30)
            .Select(_ => new Node { Info = new(true, true, false, false) });
        var root = new Node { Children = [.. earlierPages, current] };
        Assert.Same(current, Assert.Single(Find(root)));
    }

    [Fact]
    public void PointerBranchIsReadBeforeOtherVisibleDocuments()
    {
        var other = new Node { Info = new(true, false, false, false) };
        var current = new Node { Info = new(true, false, true, false) };
        var root = new Node { Children = [other, current] };
        Assert.Equal(new[] { current, other }, Find(root));
    }

    [Fact]
    public void DetachedOrPasswordBranchesDoNotHideCurrentPageOrExposeChildren()
    {
        var password = new Node { Info = new(false, false, true, true), Children = [new Node { Info = new(true, false, true, false) }] };
        var current = new Node { Info = new(true, false, true, false) };
        var root = new Node { Children = [new Node { IsDetached = true }, password, current] };
        Assert.Same(current, Assert.Single(Find(root)));
        Assert.Equal(0, password.ChildrenRequests);
    }

    [Fact]
    public void DeepVisibleRendererTreeIsNotLimitedToFirstEightLevels()
    {
        var document = new Node { Info = new(true, false, true, false) };
        var root = document;
        for (var depth = 0; depth < 20; depth++)
        {
            root = new Node { Children = [root] };
        }
        Assert.Same(document, Assert.Single(Find(root)));
    }

    [Fact]
    public void LargeTreeHasBoundedTraversalWork()
    {
        var root = new Node { Children = Enumerable.Range(0, 10_000).Select(_ => new Node()).ToArray() };
        var reads = 0;
        Assert.Empty(VisibleDocumentSearch.Find(root, node => { reads++; return node.Info; }, node => node.Children, CancellationToken.None));
        Assert.InRange(reads, 1, VisibleDocumentSearch.MaximumNodes);
    }

    [Fact]
    public void CancellationOfDocumentScanIsNotSwallowed()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => VisibleDocumentSearch.Find(
            new Node(), node => node.Info, node => node.Children, cancellation.Token).ToArray());
    }

    private static Node[] Find(Node root) => VisibleDocumentSearch.Find(root,
        node => node.IsDetached ? throw new ElementNotAvailableException() : node.Info,
        node => { node.ChildrenRequests++; return node.Children; }, CancellationToken.None).ToArray();

    private sealed class Node
    {
        public DocumentNodeInfo Info { get; init; } = new(false, false, true, false);
        public Node[] Children { get; init; } = [];
        public bool IsDetached { get; init; }
        public int ChildrenRequests { get; set; }
    }
}
