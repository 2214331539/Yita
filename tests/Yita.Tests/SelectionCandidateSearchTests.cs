using System.Windows.Automation;
using Yita.Selection;

namespace Yita.Tests;

public sealed class SelectionCandidateSearchTests
{
    [Fact]
    public void DetachedHitAfterZoomStillReadsFreshFocusedDocument()
    {
        var oldGlyph = new Node { IsDetached = true };
        var newDocument = new Node { Text = "new viewport selection" };

        Assert.Equal("new viewport selection", Read(oldGlyph, newDocument)?.Text);
        Assert.Equal(0, oldGlyph.ReadCount);
    }

    [Fact]
    public void DetachedAncestorDiscardsOldPathButStillSearchesCurrentDocument()
    {
        var oldGlyph = new Node { Text = "stale", Parent = new Node { IsDetached = true } };
        var newDocument = new Node { Text = "selection after scroll" };

        Assert.Equal("selection after scroll", Read(oldGlyph, null, newDocument)?.Text);
        Assert.Equal(0, oldGlyph.ReadCount);
    }

    [Fact]
    public void PasswordAncestorStopsEveryFallbackBeforeReadingText()
    {
        var protectedChild = new Node { Text = "private", Parent = new Node { Access = SelectionNodeAccess.Protected } };
        var unrelatedDocument = new Node { Text = "unrelated" };

        Assert.Null(Read(protectedChild, unrelatedDocument, unrelatedDocument));
        Assert.Equal(0, protectedChild.ReadCount);
        Assert.Equal(0, unrelatedDocument.ReadCount);
    }

    [Fact]
    public void DetachedFocusAndOneReplacedDocumentDoNotHideLaterVisibleDocument()
    {
        var fresh = new Node { Text = "page 24" };
        Assert.Equal("page 24", Read(null, new Node { IsDetached = true }, new Node { ThrowsDuringRead = true }, fresh)?.Text);
    }

    [Fact]
    public void ProtectedDocumentBranchDoesNotExposeTextThroughFallback()
    {
        var protectedDocument = new Node { Text = "private", Parent = new Node { Access = SelectionNodeAccess.Protected } };
        var visible = new Node { Text = "public" };
        Assert.Equal("public", Read(null, null, protectedDocument, visible)?.Text);
        Assert.Equal(0, protectedDocument.ReadCount);
    }

    [Fact]
    public void EachGestureUsesFreshNodesAfterRendererReplacesItsTree()
    {
        var first = new Node { Text = "before scroll" };
        Assert.Equal("before scroll", Read(first, null)?.Text);
        first.IsDetached = true;
        var second = new Node { Text = "after zoom" };
        Assert.Equal("after zoom", Read(first, second)?.Text);
        second.IsDetached = true;
        Assert.Equal("after scrolling again", Read(first, second, new Node { Text = "after scrolling again" })?.Text);
    }

    [Fact]
    public void UnverifiedAncestorBeyondDepthBudgetIsNotRead()
    {
        var leaf = new Node { Text = "must not read" };
        var parent = leaf;
        for (var i = 0; i < UiaSelectionReader.MaximumAncestorDepth; i++)
        {
            parent.Parent = new Node();
            parent = parent.Parent;
        }

        Assert.Null(Read(leaf, null));
        Assert.Equal(0, leaf.ReadCount);
    }

    [Fact]
    public void CancellationStopsTraversalBeforeFallbackOrTextRead()
    {
        var node = new Node { Text = "selected" };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => SelectionCandidateSearch.Read(
            node, () => node, n => n.Access, n => n.Parent,
            n => new SelectionCapture(n.Text!), () => new[] { node }, cancellation.Token));
    }

    private static SelectionCapture? Read(Node? hit, Node? focused, params Node[] documents) =>
        SelectionCandidateSearch.Read(hit, () => focused,
            node => node.IsDetached ? throw new ElementNotAvailableException() : node.Access,
            node => node.Parent,
            node =>
            {
                node.ReadCount++;
                if (node.ThrowsDuringRead) { throw new ElementNotAvailableException(); }
                return node.Text is null ? null : new SelectionCapture(node.Text);
            },
            () => documents, CancellationToken.None);

    private sealed class Node
    {
        public Node? Parent { get; set; }
        public SelectionNodeAccess Access { get; init; } = SelectionNodeAccess.Readable;
        public bool IsDetached { get; set; }
        public bool ThrowsDuringRead { get; init; }
        public string? Text { get; init; }
        public int ReadCount { get; set; }
    }
}
