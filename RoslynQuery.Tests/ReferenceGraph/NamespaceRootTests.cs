using System.Linq;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;

using RoslynQuery.ReferenceGraph;

using Xunit;

namespace RoslynQuery.Tests;

public class NamespaceRootTests
{
    private const string Source = """
        namespace Outer
        {
            namespace Inner
            {
                public class Holder { }
            }

            public class Beta { }

            public class Alpha { }
        }

        namespace Use
        {
            public class User
            {
                public Outer.Inner.Holder Make() => null;
            }
        }
        """;

    private static async Task<(Solution Solution, Compilation Compilation)> FixtureAsync()
    {
        var solution = TestSolutions.Create(("Namespaces.cs", Source));
        var compilation = await solution.Projects.Single().GetCompilationAsync(TestContext.Current.CancellationToken);

        return (solution, compilation);
    }

    private static INamespaceSymbol Namespace(Compilation compilation, string dotted) =>
        dotted.Split('.').Aggregate(compilation.GlobalNamespace, (current, part) => current.GetNamespaceMembers().Single(n => n.Name == part));

    [Fact]
    public async Task ANamespaceIdentity_RoundTrips()
    {
        var (solution, compilation) = await FixtureAsync();
        var inner = Namespace(compilation, "Outer.Inner");

        var identity = SymbolIdentity.Create(inner, solution, solution.ProjectIds.Single());
        var resolved = await identity.ResolveAsync(solution, TestContext.Current.CancellationToken);

        Assert.Equal("N:Outer.Inner", identity.DeclarationId);
        Assert.True(SymbolEqualityComparer.Default.Equals(inner, resolved));
    }

    [Fact]
    public async Task ANamespace_IsARootButNeverARow()
    {
        var (_, compilation) = await FixtureAsync();
        var inner = Namespace(compilation, "Outer.Inner");

        Assert.True(SymbolResolver.IsSupportedRoot(inner));
        Assert.False(SymbolResolver.IsGraphTarget(inner));
        Assert.False(SymbolResolver.IsSupportedRoot(compilation.GlobalNamespace));
    }

    [Fact]
    public async Task Contains_ListsSubNamespacesBeforeTypes()
    {
        var (solution, compilation) = await FixtureAsync();

        var result = await ReferenceGraphEngine.RunAsync(
            ReferenceAnalyzerKind.Contains, Namespace(compilation, "Outer"), solution, null, null, TestContext.Current.CancellationToken);

        Assert.Equal(["Inner", "Alpha", "Beta"], result.Rows.Select(r => r.DisplayText));
        Assert.Equal(SymbolGlyph.Namespace, result.Rows[0].Glyph);
    }

    [Fact]
    public async Task ASubNamespaceRow_OffersNamespaceBranches()
    {
        var (solution, compilation) = await FixtureAsync();

        var result = await ReferenceGraphEngine.RunAsync(
            ReferenceAnalyzerKind.Contains, Namespace(compilation, "Outer"), solution, null, null, TestContext.Current.CancellationToken);

        Assert.Equal(
            [ReferenceAnalyzerKind.UsedBy, ReferenceAnalyzerKind.Contains],
            result.Rows[0].Children.Select(c => c.Analyzer.Value));
    }

    /// <summary>Every namespace occurrence is a type reference, which the ordinary Used By mask excludes.</summary>
    [Fact]
    public async Task UsedBy_OnANamespace_FindsTheMemberThatNamesIt()
    {
        var (solution, compilation) = await FixtureAsync();

        var result = await ReferenceGraphEngine.RunAsync(
            ReferenceAnalyzerKind.UsedBy, Namespace(compilation, "Outer.Inner"), solution, null, null, TestContext.Current.CancellationToken);

        Assert.Equal(["User.Make()"], result.Rows.Select(r => r.DisplayText));
    }

    [Fact]
    public async Task Uses_NeverProducesANamespaceRow()
    {
        var (solution, compilation) = await FixtureAsync();
        var make = compilation.GetTypeByMetadataName("Use.User").GetMembers("Make").Single();

        var result = await ReferenceGraphEngine.RunAsync(
            ReferenceAnalyzerKind.Uses, make, solution, null, null, TestContext.Current.CancellationToken);

        Assert.Contains(result.Rows, r => r.DisplayText == "Holder");
        Assert.DoesNotContain(result.Rows, r => r.Glyph == SymbolGlyph.Namespace);
    }
}
