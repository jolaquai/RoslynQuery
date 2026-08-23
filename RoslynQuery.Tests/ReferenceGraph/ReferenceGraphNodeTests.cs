using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

using RoslynQuery.ReferenceGraph;

using Xunit;

namespace RoslynQuery.Tests;

public class ReferenceGraphNodeTests
{
    private const string Source = """
        namespace N
        {
            public class Outer
            {
                public int Field;
                public void First() { }
                public void First(int overloaded) { }
                public void Second() { }
            }
        }
        """;

    private static async Task<(Solution Solution, Compilation Compilation, INamedTypeSymbol Type)> FixtureAsync()
    {
        var solution = TestSolutions.Create(("Outer.cs", Source));
        var project = solution.Projects.Single();
        var compilation = await project.GetCompilationAsync(TestContext.Current.CancellationToken);

        return (solution, compilation, compilation.GetTypeByMetadataName("N.Outer"));
    }

    private static ReferenceGraphNode NodeFor(ISymbol symbol, Solution solution, ReferenceGraphNode parent = null) =>
        new ReferenceGraphNode(
            symbol.Name,
            SymbolIdentity.Create(symbol, solution, solution.Projects.Single().Id),
            SymbolGlyphs.For(symbol),
            ReferenceDirection.Incoming,
            parent: parent);

    [Fact]
    public async Task Identity_RoundTripsBackToTheOriginalSymbol()
    {
        var (solution, compilation, type) = await FixtureAsync();
        var method = type.GetMembers("First").OfType<IMethodSymbol>().First(m => m.Parameters.Length == 0);

        var node = NodeFor(method, solution);
        var resolved = await node.Identity.ResolveAsync(solution, TestContext.Current.CancellationToken);

        Assert.True(SymbolEqualityComparer.Default.Equals(method, resolved));
    }

    [Fact]
    public async Task Identity_DistinguishesOverloads()
    {
        var (solution, _, type) = await FixtureAsync();
        var overloads = type.GetMembers("First").OfType<IMethodSymbol>().ToList();

        var first = SymbolIdentity.Create(overloads[0], solution, solution.Projects.Single().Id);
        var second = SymbolIdentity.Create(overloads[1], solution, solution.Projects.Single().Id);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task Identity_OfTheSameSymbolTwice_CompareEqual()
    {
        var (solution, _, type) = await FixtureAsync();
        var field = type.GetMembers("Field").Single();
        var projectId = solution.Projects.Single().Id;

        Assert.Equal(SymbolIdentity.Create(field, solution, projectId), SymbolIdentity.Create(field, solution, projectId));
    }

    [Fact]
    public async Task HasAncestor_FindsASymbolTwoLevelsUp()
    {
        var (solution, _, type) = await FixtureAsync();
        var first = type.GetMembers("First").First();
        var second = type.GetMembers("Second").Single();
        var field = type.GetMembers("Field").Single();

        var grandparent = NodeFor(first, solution);
        var parent = NodeFor(second, solution, grandparent);
        var child = NodeFor(field, solution, parent);

        Assert.True(child.HasAncestor(grandparent.Identity));
    }

    [Fact]
    public async Task HasAncestor_ReturnsFalseForAnUnrelatedSymbol()
    {
        var (solution, _, type) = await FixtureAsync();
        var first = type.GetMembers("First").First();
        var second = type.GetMembers("Second").Single();
        var field = type.GetMembers("Field").Single();

        var parent = NodeFor(first, solution);
        var child = NodeFor(second, solution, parent);

        Assert.False(child.HasAncestor(SymbolIdentity.Create(field, solution, solution.Projects.Single().Id)));
    }

    [Fact]
    public async Task HasAncestor_MatchesTheNodesOwnSymbol()
    {
        var (solution, _, type) = await FixtureAsync();
        var node = NodeFor(type.GetMembers("Second").Single(), solution);

        Assert.True(node.HasAncestor(node.Identity));
    }

    [Fact]
    public async Task Constructor_SeedsAPlaceholderChildSoTheExpanderShows()
    {
        var (solution, _, type) = await FixtureAsync();
        var node = NodeFor(type.GetMembers("Second").Single(), solution);

        var placeholder = Assert.Single(node.Children);
        Assert.True(placeholder.IsMessage);
        Assert.Equal(ReferenceGraphNode.SearchingText, placeholder.DisplayText);
        Assert.False(node.IsLoaded);
    }

    [Fact]
    public void MessageNode_IsNotExpandableAndHasNoChildren()
    {
        var message = ReferenceGraphNode.CreateMessage("12 more...");

        Assert.True(message.IsMessage);
        Assert.False(message.IsExpandable);
        Assert.Empty(message.Children);
    }

    [Fact]
    public async Task SetChildren_ReplacesThePlaceholderAndMarksTheNodeLoaded()
    {
        var (solution, _, type) = await FixtureAsync();
        var node = NodeFor(type.GetMembers("Second").Single(), solution);

        node.SetChildren([ReferenceGraphNode.CreateMessage("nothing found", node)]);

        Assert.Equal("nothing found", Assert.Single(node.Children).DisplayText);
        Assert.True(node.IsLoaded);
    }

    [Fact]
    public async Task DocumentIdAndSpan_ComeFromTheFirstLocation()
    {
        var (solution, _, type) = await FixtureAsync();
        var documentId = solution.Projects.Single().DocumentIds[0];
        var locations = new List<ReferenceLocationInfo>
        {
            new ReferenceLocationInfo(documentId, new TextSpan(10, 5), ReferenceUsageKind.Invocation),
            new ReferenceLocationInfo(documentId, new TextSpan(40, 5), ReferenceUsageKind.Invocation)
        };

        var symbol = type.GetMembers("Second").Single();
        var node = new ReferenceGraphNode(
            symbol.Name, SymbolIdentity.Create(symbol, solution, solution.Projects.Single().Id),
            SymbolGlyphs.For(symbol), ReferenceDirection.Incoming, locations);

        Assert.Equal(documentId, node.DocumentId);
        Assert.Equal(new TextSpan(10, 5), node.Span);
    }

    [Fact]
    public async Task PropertyChanged_FiresWhenIsRecursiveFlips()
    {
        var (solution, _, type) = await FixtureAsync();
        var node = NodeFor(type.GetMembers("Second").Single(), solution);

        var raised = new List<string>();
        node.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        node.IsRecursive = true;
        node.IsRecursive = true;

        Assert.Equal([nameof(ReferenceGraphNode.IsRecursive)], raised);
    }

    // Flag values rather than the enum itself: InlineData arguments have to be at least as
    // accessible as the public test method, and ReferenceUsageKind is internal.
    [Theory]
    [InlineData(new[] { 1 }, "1 invocation")]
    [InlineData(new[] { 1, 1 }, "2 invocations")]
    [InlineData(new[] { 2, 4 }, "2 refs (1 read, 1 write)")]
    [InlineData(new[] { 6 }, "1 ref (1 read, 1 write)")]
    [InlineData(new[] { 16, 16 }, "2 type references")]
    [InlineData(new[] { 1, 2, 8 }, "3 refs (1 invocation, 1 read, 1 construction)")]
    public void Describe_ReportsCountsAndBreakdown(int[] kinds, string expected)
    {
        var locations = kinds
            .Select(k => new ReferenceLocationInfo(null, default, (ReferenceUsageKind)k))
            .ToList();

        Assert.Equal(expected, ReferenceGraphNode.Describe(locations));
    }

    [Fact]
    public void Describe_WithNoLocations_IsNull() => Assert.Null(ReferenceGraphNode.Describe([]));

    [Fact]
    public async Task Glyph_MapsEachSymbolShapeToItsOwnIcon()
    {
        var (_, _, type) = await FixtureAsync();

        Assert.Equal(SymbolGlyph.Class, SymbolGlyphs.For(type));
        Assert.Equal(SymbolGlyph.Method, SymbolGlyphs.For(type.GetMembers("Second").Single()));
        Assert.Equal(SymbolGlyph.Field, SymbolGlyphs.For(type.GetMembers("Field").Single()));
        Assert.Equal(SymbolGlyph.Constructor, SymbolGlyphs.For(type.InstanceConstructors.Single()));
        Assert.Equal(SymbolGlyph.Unknown, SymbolGlyphs.For(null));
    }
}
