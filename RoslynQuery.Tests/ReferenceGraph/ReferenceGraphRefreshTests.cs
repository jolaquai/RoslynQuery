using System.Linq;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;

using RoslynQuery.ReferenceGraph;

using Xunit;

namespace RoslynQuery.Tests;

// The refresh walk must land on analyzer rows only. Symbol rows own no fetch, so re-running one would
// replace its branches with a result set and wipe the tree out.'
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
            ReferenceGraphDisplay.Of(symbol), identity, SymbolGlyphs.For(symbol), ReferenceAnalyzers.For(symbol));
    }

    [Fact]
    public async Task CreateRoot_BuildsTheApplicableAnalyzerBranches()
    {
        var root = await RootAsync();

        // Target is a plain private method: Uses and Used By, nothing else applies.
        Assert.Equal(["Uses", "Used By"], root.Children.Select(c => c.DisplayText));
        Assert.All(root.Children, c => Assert.Equal(NodeRole.Analyzer, c.Role));
        Assert.True(root.IsExpanded);
    }

    [Fact]
    public async Task CreateRoot_MakesTheRootUnfetchable()
    {
        var root = await RootAsync();

        // Fetchable would mean a refresh re-runs the engine on it and overwrites its branches.
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
        var child = ReferenceGraphNode.CreateSymbol("child", branch.Identity, SymbolGlyph.Method, [], parent: branch);
        branch.SetChildren([child]);
        branch.IsExpanded = true;

        child.SetChildren([ReferenceGraphNode.CreateMessage("grandchild", child)]);
        child.IsExpanded = true;

        // The branch's children get replaced wholesale, so re-reading the child too is wasted work.
        Assert.Same(branch, Assert.Single(ReferenceGraphNode.ShallowestExpanded([root])));
    }

    [Fact]
    public async Task ShallowestExpanded_DescendsPastSymbolRowsToTheirBranches()
    {
        var (solution, _, _) = await FixtureAsync();
        var compilation = await solution.Projects.Single().GetCompilationAsync(TestContext.Current.CancellationToken);
        var target = compilation.GetTypeByMetadataName("C").GetMembers("Target").First();

        var nodes = await ReferenceGraphEngine.FindIncomingAsync(
            target, solution, null, ReferenceUsageKind.Invocation, null, TestContext.Current.CancellationToken);

        var row = Assert.Single(nodes);
        var branch = row.Children[0];
        branch.SetChildren([]);
        branch.IsExpanded = true;

        // The symbol row itself is never a refresh target; its analyzer branch is.
        Assert.Same(branch, Assert.Single(ReferenceGraphNode.ShallowestExpanded(nodes)));
    }

    [Fact]
    public async Task ResetToUnloaded_ReplacesChildrenWithThePlaceholderAndClearsLoadedAndExpanded()
    {
        var branch = (await RootAsync()).Children[0];
        branch.SetChildren([ReferenceGraphNode.CreateMessage("a row", branch)]);
        branch.IsExpanded = true;

        branch.ResetToUnloaded();

        Assert.False(branch.IsLoaded);
        Assert.False(branch.IsExpanded);
        Assert.Equal(ReferenceGraphNode.SearchingText, Assert.Single(branch.Children).DisplayText);
    }

    [Fact]
    public async Task ResetToUnloaded_LeavesASymbolRowAlone()
    {
        var root = await RootAsync();

        root.ResetToUnloaded();

        // A symbol row has no fetch to discard, and clearing it would drop its branches for good.
        Assert.True(root.IsLoaded);
        Assert.Equal(2, root.Children.Count);
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
        var child = ReferenceGraphNode.CreateSymbol("child", branch.Identity, SymbolGlyph.Method, [], parent: branch);
        branch.SetChildren([child]);

        child.SetChildren([ReferenceGraphNode.CreateMessage("grandchild", child)]);

        Assert.Same(branch, Assert.Single(ReferenceGraphNode.ShallowestLoaded([root])));
    }
}
