using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;

using RoslynQuery.ReferenceGraph;

using Xunit;

namespace RoslynQuery.Tests;

public class AnalyzerDispatchTests
{
    private const string Source = """
        using System;

        namespace N
        {
            public interface IShape { double Area(); }

            public class Base : IShape
            {
                public int Field;
                public virtual double Area() => 0;
            }

            public class Derived : Base
            {
                public override double Area() => 1;
                public Base Make() => new Base();
            }

            public static class Extensions
            {
                public static int Twice(this Base value) => 2;
            }
        }
        """;

    private static async Task<(Solution Solution, Compilation Compilation)> FixtureAsync()
    {
        var solution = TestSolutions.Create(("Shapes.cs", Source));
        var compilation = await solution.Projects.Single().GetCompilationAsync(TestContext.Current.CancellationToken);

        return (solution, compilation);
    }

    /// <summary>A kind added to the enum without a dispatch case throws, rather than looking like an empty branch.</summary>
    [Fact]
    public async Task EveryAnalyzerKind_IsWired()
    {
        var (solution, compilation) = await FixtureAsync();
        var type = compilation.GetTypeByMetadataName("N.Derived");
        var method = type.GetMembers("Area").Single();

        foreach (ReferenceAnalyzerKind kind in Enum.GetValues(typeof(ReferenceAnalyzerKind)))
        {
            foreach (var symbol in new ISymbol[] { type, method })
            {
                var result = await ReferenceGraphEngine.RunAsync(
                    kind, symbol, solution, null, null, TestContext.Current.CancellationToken);

                Assert.NotNull(result.Rows);
            }
        }
    }

    [Fact]
    public async Task AnEmptyBranch_HasNoChildrenAndNoHeaderSuffix()
    {
        var (solution, compilation) = await FixtureAsync();
        var field = compilation.GetTypeByMetadataName("N.Base").GetMembers("Field").Single();

        var result = await ReferenceGraphEngine.RunAsync(
            ReferenceAnalyzerKind.AssignedBy, field, solution, null, null, TestContext.Current.CancellationToken);

        var branch = ReferenceGraphNode.CreateAnalyzer(
            ReferenceAnalyzerKind.AssignedBy,
            ReferenceGraphNode.CreateSymbol("Field", default, SymbolGlyph.Field, []));

        branch.ApplyResults(result);

        Assert.Empty(result.Rows);
        Assert.Empty(branch.Children);
        Assert.Null(branch.SecondaryText);
        Assert.True(branch.IsLoaded);
    }

    [Fact]
    public async Task ANonEmptyBranch_ReportsCountAndElapsedTheWayIlspyDoes()
    {
        var (solution, compilation) = await FixtureAsync();
        var baseType = compilation.GetTypeByMetadataName("N.Base");

        var result = await ReferenceGraphEngine.RunAsync(
            ReferenceAnalyzerKind.DerivedTypes, baseType, solution, null, null, TestContext.Current.CancellationToken);

        var branch = ReferenceGraphNode.CreateAnalyzer(
            ReferenceAnalyzerKind.DerivedTypes,
            ReferenceGraphNode.CreateSymbol("Base", default, SymbolGlyph.Class, []));

        branch.ApplyResults(result);

        Assert.Matches(new Regex(@"^\(\d+ in \d+ ms\)$"), branch.SecondaryText);
        Assert.StartsWith($"({result.Rows.Count} in ", branch.SecondaryText);
        Assert.Equal(result.Rows.Count, branch.Children.Count);
    }

    [Fact]
    public async Task Uses_DispatchesToTheOutgoingWalk()
    {
        var (solution, compilation) = await FixtureAsync();
        var make = compilation.GetTypeByMetadataName("N.Derived").GetMembers("Make").Single();

        var result = await ReferenceGraphEngine.RunAsync(
            ReferenceAnalyzerKind.Uses, make, solution, null, null, TestContext.Current.CancellationToken);

        Assert.Contains(result.Rows, r => r.DisplayText.Contains("Base"));
    }

    [Fact]
    public async Task DerivedTypes_DispatchesToTheHierarchyAnalyzer()
    {
        var (solution, compilation) = await FixtureAsync();

        var result = await ReferenceGraphEngine.RunAsync(
            ReferenceAnalyzerKind.DerivedTypes, compilation.GetTypeByMetadataName("N.Base"), solution, null, null,
            TestContext.Current.CancellationToken);

        Assert.Equal("Derived", Assert.Single(result.Rows).DisplayText);
    }

    [Fact]
    public async Task ExtensionMethods_DispatchesToTheScan()
    {
        var (solution, compilation) = await FixtureAsync();

        var result = await ReferenceGraphEngine.RunAsync(
            ReferenceAnalyzerKind.ExtensionMethods, compilation.GetTypeByMetadataName("N.Base"), solution, null, null,
            TestContext.Current.CancellationToken);

        Assert.Contains(result.Rows, r => r.DisplayText.Contains("Twice"));
    }

    [Fact]
    public async Task ANullSymbol_ReturnsAnEmptyResultRatherThanThrowing()
    {
        var (solution, _) = await FixtureAsync();

        var result = await ReferenceGraphEngine.RunAsync(
            ReferenceAnalyzerKind.UsedBy, null, solution, null, null, TestContext.Current.CancellationToken);

        Assert.Empty(result.Rows);
        Assert.Equal(0, result.ElapsedMilliseconds);
    }
}
