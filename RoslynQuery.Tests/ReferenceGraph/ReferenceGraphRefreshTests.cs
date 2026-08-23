using System.Linq;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;

using RoslynQuery.ReferenceGraph;

using Xunit;

namespace RoslynQuery.Tests;

// Changing the filter re-reads every expanded row. The root is not one of them: its children are the
// two direction branches, and re-fetching it replaced them with a plain incoming result, wiping the
// tree out.
public class ReferenceGraphRefreshTests
{
    private static async Task<(Solution Solution, SymbolIdentity Identity, ISymbol Symbol)> FixtureAsync()
    {
        var solution = TestSolutions.Create(
            ("A.cs", "class C { void Target() { } void Caller() { Target(); } }"));

        var compilation = await solution.Projects.Single().GetCompilationAsync(TestContext.Current.CancellationToken);
        var symbol = compilation.GetTypeByMetadataName("C").GetMembers("Target").First();

        return (solution, SymbolIdentity.Create(symbol, solution, solution.ProjectIds.First()), symbol);
    }

    private static async Task<ReferenceGraphNode> RootAsync()
    {
        var (_, identity, symbol) = await FixtureAsync();

        return ReferenceGraphNode.CreateRoot(
            ReferenceGraphDisplay.Of(symbol), symbol.Name, identity, SymbolGlyphs.For(symbol));
    }

    [Fact]
    public async Task CreateRoot_BuildsBothDirectionBranches()
    {
        var root = await RootAsync();

        Assert.Equal(2, root.Children.Count);
        Assert.Equal("References To 'Target'", root.Children[0].DisplayText);
        Assert.Equal(ReferenceDirection.Incoming, root.Children[0].Direction);
        Assert.Equal("References From 'Target'", root.Children[1].DisplayText);
        Assert.Equal(ReferenceDirection.Outgoing, root.Children[1].Direction);
        Assert.True(root.IsExpanded);
    }

    [Fact]
    public async Task CreateRoot_MakesTheRootUnfetchable()
    {
        var root = await RootAsync();

        // Fetchable would mean a refresh re-runs the engine on it and overwrites both branches.
        Assert.False(root.IsExpandable);
        Assert.True(root.IsLoaded);
    }

    [Fact]
    public async Task ShallowestExpanded_SkipsTheRootAndReturnsItsExpandedBranches()
    {
        var root = await RootAsync();

        foreach (var branch in root.Children)
        {
            branch.SetChildren([ReferenceGraphNode.CreateMessage("a row", branch)]);
            branch.IsExpanded = true;
        }

        var refreshed = ReferenceGraphNode.ShallowestExpanded([root]).ToList();

        Assert.Equal(2, refreshed.Count);
        Assert.DoesNotContain(root, refreshed);
        Assert.Equal(root.Children, refreshed);
    }

    [Fact]
    public async Task ShallowestExpanded_IgnoresABranchThatWasNeverOpened()
    {
        var root = await RootAsync();

        var opened = root.Children[0];
        opened.SetChildren([ReferenceGraphNode.CreateMessage("a row", opened)]);
        opened.IsExpanded = true;

        Assert.Same(opened, Assert.Single(ReferenceGraphNode.ShallowestExpanded([root])));
    }

    [Fact]
    public async Task ShallowestExpanded_StopsAtTheOutermostExpandedRow()
    {
        var root = await RootAsync();

        var branch = root.Children[0];
        var child = new ReferenceGraphNode("child", branch.Identity, SymbolGlyph.Method, ReferenceDirection.Incoming, parent: branch);
        branch.SetChildren([child]);
        branch.IsExpanded = true;

        child.SetChildren([ReferenceGraphNode.CreateMessage("grandchild", child)]);
        child.IsExpanded = true;

        // The branch's children get replaced wholesale, so re-reading the child too is wasted work.
        Assert.Same(branch, Assert.Single(ReferenceGraphNode.ShallowestExpanded([root])));
    }

    [Fact]
    public async Task ShallowestExpanded_SkipsALocationsBranch()
    {
        var (solution, _, _) = await FixtureAsync();
        var compilation = await solution.Projects.Single().GetCompilationAsync(TestContext.Current.CancellationToken);
        var target = compilation.GetTypeByMetadataName("C").GetMembers("Target").First();

        var nodes = await ReferenceGraphEngine.FindIncomingAsync(
            target, solution, null, ReferenceUsageKind.Invocation, null, TestContext.Current.CancellationToken);

        var row = Assert.Single(nodes);
        row.SetChildren([]);
        row.IsExpanded = true;

        Assert.Same(row, Assert.Single(ReferenceGraphNode.ShallowestExpanded(nodes)));
    }

    [Fact]
    public void ResetToUnloaded_ReplacesChildrenWithThePlaceholderAndClearsLoadedAndExpanded()
    {
        var parent = new ReferenceGraphNode("row", default, SymbolGlyph.Method, ReferenceDirection.Incoming);
        parent.SetChildren([ReferenceGraphNode.CreateMessage("a row", parent)]);
        parent.IsExpanded = true;

        parent.ResetToUnloaded();

        Assert.False(parent.IsLoaded);
        Assert.False(parent.IsExpanded);
        Assert.Equal(ReferenceGraphNode.SearchingText, Assert.Single(parent.Children).DisplayText);
    }

    [Fact]
    public async Task ShallowestLoaded_ReturnsALoadedRowEvenWhenCollapsed()
    {
        var root = await RootAsync();

        var branch = root.Children[0];
        branch.SetChildren([ReferenceGraphNode.CreateMessage("a row", branch)]);
        branch.IsExpanded = false;

        Assert.Same(branch, Assert.Single(ReferenceGraphNode.ShallowestLoaded([root])));
    }

    [Fact]
    public async Task ShallowestLoaded_SkipsAnUnloadedRow()
    {
        var root = await RootAsync();

        Assert.Empty(ReferenceGraphNode.ShallowestLoaded([root]));
    }

    [Fact]
    public async Task ShallowestLoaded_StopsAtTheOutermostLoadedRow()
    {
        var root = await RootAsync();

        var branch = root.Children[0];
        var child = new ReferenceGraphNode("child", branch.Identity, SymbolGlyph.Method, ReferenceDirection.Incoming, parent: branch);
        branch.SetChildren([child]);

        child.SetChildren([ReferenceGraphNode.CreateMessage("grandchild", child)]);

        Assert.Same(branch, Assert.Single(ReferenceGraphNode.ShallowestLoaded([root])));
    }
}
