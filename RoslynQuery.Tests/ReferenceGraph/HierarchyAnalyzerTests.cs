using System.Linq;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

using RoslynQuery.ReferenceGraph;

using Xunit;

namespace RoslynQuery.Tests;

public class HierarchyAnalyzerTests
{
    private const string Source = """
        using System;

        namespace N
        {
            public interface IShape { double Area(); }

            public interface IDerivedShape : IShape { }

            public abstract class Base : IShape
            {
                public abstract double Area();
                public virtual void Draw() { }
                public void Plainly() { }
            }

            public class Middle : Base
            {
                public override double Area() => 1;
                public override void Draw() { }
            }

            public sealed class Leaf : Middle
            {
                public override double Area() => 2;
                public override void Draw() { }
            }

            public class Direct : IShape
            {
                public double Area() => 3;
            }
        }
        """;

    private static async Task<(Solution Solution, Compilation Compilation)> FixtureAsync()
    {
        var solution = TestSolutions.Create(("Shapes.cs", Source));
        var compilation = await solution.Projects.Single().GetCompilationAsync(TestContext.Current.CancellationToken);

        return (solution, compilation);
    }

    private static ISymbol Member(Compilation compilation, string type, string name) =>
        compilation.GetTypeByMetadataName("N." + type).GetMembers(name).First();

    [Fact]
    public async Task OverriddenBy_OnTheBase_ReturnsEveryDescendant()
    {
        var (solution, compilation) = await FixtureAsync();

        var nodes = await HierarchyAnalyzers.FindOverriddenByAsync(
            Member(compilation, "Base", "Draw"), solution, null, null, TestContext.Current.CancellationToken);

        Assert.Equal(["Leaf.Draw()", "Middle.Draw()"], nodes.Select(n => n.DisplayText));
    }

    [Fact]
    public async Task Overrides_OnTheLeaf_ReturnsTheChainNearestFirst()
    {
        var (solution, compilation) = await FixtureAsync();

        var nodes = await HierarchyAnalyzers.FindOverridesAsync(
            Member(compilation, "Leaf", "Area"), solution, null, TestContext.Current.CancellationToken);

        Assert.Equal(["Middle.Area()", "Base.Area()"], nodes.Select(n => n.DisplayText));
    }

    [Fact]
    public async Task Overrides_OnAMethodThatOverridesNothing_IsEmpty()
    {
        var (solution, compilation) = await FixtureAsync();

        Assert.Empty(await HierarchyAnalyzers.FindOverridesAsync(
            Member(compilation, "Base", "Plainly"), solution, null, TestContext.Current.CancellationToken));
    }

    /// <summary>The regression the override-chain walk exists for; removing the walk makes this return nothing.</summary>
    [Fact]
    public async Task Implements_OnAnOverrideTwoLevelsRemoved_StillFindsTheInterfaceMember()
    {
        var (solution, compilation) = await FixtureAsync();

        var nodes = await HierarchyAnalyzers.FindImplementsAsync(
            Member(compilation, "Leaf", "Area"), solution, null, null, TestContext.Current.CancellationToken);

        Assert.Equal("IShape.Area()", Assert.Single(nodes).DisplayText);
    }

    /// <summary>Pins the Roslyn behaviour the walk works around, so a future change to it is visible here.</summary>
    [Fact]
    public async Task FindImplementedInterfaceMembers_CalledDirectlyOnAnOverride_ReturnsNothing()
    {
        var (solution, compilation) = await FixtureAsync();

        var direct = await SymbolFinder.FindImplementedInterfaceMembersAsync(
            Member(compilation, "Leaf", "Area"), solution, null, TestContext.Current.CancellationToken);

        Assert.Empty(direct);
    }

    [Fact]
    public async Task Implements_OnTheImplementingMemberItself_FindsTheInterfaceMember()
    {
        var (solution, compilation) = await FixtureAsync();

        var nodes = await HierarchyAnalyzers.FindImplementsAsync(
            Member(compilation, "Direct", "Area"), solution, null, null, TestContext.Current.CancellationToken);

        Assert.Equal("IShape.Area()", Assert.Single(nodes).DisplayText);
    }

    [Fact]
    public async Task ImplementedBy_OnAnInterfaceMember_ReturnsOneRowPerImplementingType()
    {
        var (solution, compilation) = await FixtureAsync();

        var nodes = await HierarchyAnalyzers.FindImplementedByAsync(
            Member(compilation, "IShape", "Area"), solution, null, null, TestContext.Current.CancellationToken);

        Assert.Equal(["Base.Area()", "Direct.Area()"], nodes.Select(n => n.DisplayText));
    }

    [Fact]
    public async Task ImplementedBy_OnAnInterfaceType_ReturnsImplementingTypes()
    {
        var (solution, compilation) = await FixtureAsync();

        var nodes = await HierarchyAnalyzers.FindImplementedByAsync(
            compilation.GetTypeByMetadataName("N.IShape"), solution, null, null, TestContext.Current.CancellationToken);

        Assert.Contains(nodes, n => n.DisplayText == "Base");
        Assert.Contains(nodes, n => n.DisplayText == "Direct");
        Assert.Contains(nodes, n => n.DisplayText == "Leaf");
    }

    [Fact]
    public async Task DerivedTypes_OnAClass_IsTransitive()
    {
        var (solution, compilation) = await FixtureAsync();

        var nodes = await HierarchyAnalyzers.FindDerivedTypesAsync(
            compilation.GetTypeByMetadataName("N.Base"), solution, null, null, TestContext.Current.CancellationToken);

        Assert.Equal(["Leaf", "Middle"], nodes.Select(n => n.DisplayText));
    }

    [Fact]
    public async Task DerivedTypes_OnAnInterface_ReturnsDerivedInterfaces()
    {
        var (solution, compilation) = await FixtureAsync();

        var nodes = await HierarchyAnalyzers.FindDerivedTypesAsync(
            compilation.GetTypeByMetadataName("N.IShape"), solution, null, null, TestContext.Current.CancellationToken);

        Assert.Equal("IDerivedShape", Assert.Single(nodes).DisplayText);
    }

    [Fact]
    public async Task DerivedTypes_OnANonType_IsEmpty()
    {
        var (solution, compilation) = await FixtureAsync();

        Assert.Empty(await HierarchyAnalyzers.FindDerivedTypesAsync(
            Member(compilation, "Base", "Draw"), solution, null, null, TestContext.Current.CancellationToken));
    }

    /// <summary>The free metadata depth this phase is built on: these branches search referenced assemblies too.</summary>
    [Fact]
    public async Task DerivedTypes_OnAFrameworkType_ReachesIntoMetadata()
    {
        var (solution, compilation) = await FixtureAsync();

        var nodes = await HierarchyAnalyzers.FindDerivedTypesAsync(
            compilation.GetTypeByMetadataName("System.IO.Stream"), solution, null, null, TestContext.Current.CancellationToken);

        Assert.Contains(nodes, n => n.DisplayText == "MemoryStream");
        Assert.Contains(nodes, n => n.DisplayText == "BufferedStream");
    }

    [Fact]
    public async Task OverriddenBy_OnAFrameworkMember_ReachesIntoMetadata()
    {
        var (solution, compilation) = await FixtureAsync();
        var streamRead = compilation.GetTypeByMetadataName("System.IO.Stream")
            .GetMembers("Read").OfType<IMethodSymbol>().First(m => m.Parameters.Length == 3);

        var nodes = await HierarchyAnalyzers.FindOverriddenByAsync(
            streamRead, solution, null, null, TestContext.Current.CancellationToken);

        Assert.Contains(nodes, n => n.DisplayText.StartsWith("MemoryStream.Read"));
    }

    [Fact]
    public async Task ARow_NavigatesToItsDeclaration()
    {
        var (solution, compilation) = await FixtureAsync();

        var nodes = await HierarchyAnalyzers.FindOverridesAsync(
            Member(compilation, "Leaf", "Area"), solution, null, TestContext.Current.CancellationToken);

        var row = nodes[0];
        var declaration = Member(compilation, "Middle", "Area").DeclaringSyntaxReferences.Single();

        Assert.NotNull(row.DocumentId);
        Assert.Equal(declaration.Span, row.Span);
    }

    /// <summary>A declaration is not a usage, so there is no "3 invocations" line to write.</summary>
    [Fact]
    public async Task ARow_HasNoUsageBreakdown()
    {
        var (solution, compilation) = await FixtureAsync();

        var nodes = await HierarchyAnalyzers.FindOverriddenByAsync(
            Member(compilation, "Base", "Draw"), solution, null, null, TestContext.Current.CancellationToken);

        Assert.All(nodes, n => Assert.Null(n.SecondaryText));
    }

    [Fact]
    public async Task ARow_OffersItsOwnAnalyzerBranches()
    {
        var (solution, compilation) = await FixtureAsync();

        var nodes = await HierarchyAnalyzers.FindOverriddenByAsync(
            Member(compilation, "Base", "Draw"), solution, null, null, TestContext.Current.CancellationToken);

        // Every result row re-analyses, which is what makes the tree recursive the way ILSpy's is.
        Assert.All(nodes, n => Assert.All(n.Children, c => Assert.Equal(NodeRole.Analyzer, c.Role)));
        Assert.Contains(nodes[0].Children, c => c.Analyzer == ReferenceAnalyzerKind.Overrides);
    }
}
