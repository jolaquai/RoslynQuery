using System.Linq;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;

using RoslynQuery.ReferenceGraph;

using Xunit;

namespace RoslynQuery.Tests;

// A row that says "3 invocations" has to be openable to reach all three, not just the first.
public class ReferenceGraphNodeLocationRowTests
{
    private const ReferenceUsageKind All =
        ReferenceUsageKind.Invocation | ReferenceUsageKind.Read | ReferenceUsageKind.Write
        | ReferenceUsageKind.Construction | ReferenceUsageKind.TypeReference;

    private static async Task<ISymbol> SymbolAsync(Solution solution, string typeName, string memberName)
    {
        var compilation = await solution.Projects.Single().GetCompilationAsync(TestContext.Current.CancellationToken);

        return compilation.GetTypeByMetadataName(typeName).GetMembers(memberName).First();
    }

    private static async Task<ReferenceGraphNode> MultiLocationRowAsync()
    {
        var solution = TestSolutions.Create(
            ("A.cs", """
                class C
                {
                    void Target() { }
                    void Caller() { Target(); Target(); Target(); }
                }
                """));

        var nodes = await ReferenceGraphEngine.FindIncomingAsync(
            await SymbolAsync(solution, "C", "Target"), solution, null, All, null, TestContext.Current.CancellationToken);

        return Assert.Single(nodes);
    }

    [Fact]
    public async Task AMultiLocationRow_GetsALocationsBranchUpFront()
    {
        var node = await MultiLocationRowAsync();

        Assert.Equal("3 invocations", node.SecondaryText);

        var branch = node.Children[0];
        Assert.Equal("Locations (3)", branch.DisplayText);
        Assert.Equal(SymbolGlyph.Locations, branch.Glyph);
        Assert.Equal(NodeRole.Locations, branch.Role);
    }

    [Fact]
    public async Task LocationsBranch_HasOneNavigableRowPerOccurrence()
    {
        var node = await MultiLocationRowAsync();

        var rows = node.Children[0].Children;

        Assert.Equal(3, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal(SymbolGlyph.Location, r.Glyph);
            Assert.NotNull(r.DocumentId);
            Assert.Contains("A.cs (", r.DisplayText);
            Assert.False(r.IsExpandable);
        });

        // Each row must point at its own occurrence, or they would all navigate to the same place.
        Assert.Equal(3, rows.Select(r => r.Span.Start).Distinct().Count());
    }

    [Fact]
    public async Task LocationsBranch_IsPreLoadedSoTheLazyFetchLeavesItAlone()
    {
        var node = await MultiLocationRowAsync();

        var branch = node.Children[0];

        Assert.True(branch.IsLoaded);
        Assert.False(branch.IsExpandable);
    }

    [Fact]
    public async Task LocationsBranch_SortsAheadOfTheAnalyzerBranches()
    {
        var node = await MultiLocationRowAsync();

        Assert.Equal("Locations (3)", node.Children[0].DisplayText);
        Assert.All(node.Children.Skip(1), c => Assert.Equal(NodeRole.Analyzer, c.Role));
    }

    [Fact]
    public async Task ASingleLocationRow_GetsNoLocationsBranch()
    {
        var solution = TestSolutions.Create(
            ("A.cs", "class C { void Target() { } void Caller() { Target(); } }"));

        var nodes = await ReferenceGraphEngine.FindIncomingAsync(
            await SymbolAsync(solution, "C", "Target"), solution, null, All, null, TestContext.Current.CancellationToken);

        // The row itself already navigates to its only occurrence.
        Assert.DoesNotContain(Assert.Single(nodes).Children, c => c.Role == NodeRole.Locations);
    }
}
