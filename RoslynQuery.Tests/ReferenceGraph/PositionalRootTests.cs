using System.Linq;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RoslynQuery.ReferenceGraph;

using Xunit;

namespace RoslynQuery.Tests;

public class PositionalRootTests
{
    private const string Source = """
        using System;

        namespace N
        {
            public class Holder<TItem>
            {
                public TItem Keep;

                public int Pick(int count)
                {
                    var running = count;
                    running = running + 1;
                    Console.WriteLine(running);

                    int Helper(int seed) => seed + running;

                    Func<int, int> doubled = x => Helper(x) + Twice(x);

                    return Helper(running);
                }

                public static int Twice(int value) => value * 2;

                public TItem Echo(TItem value) => value;
            }
        }
        """;

    private const string PickLabel = "Holder<TItem>.Pick(int)";

    private static async Task<(Solution Solution, SemanticModel Model, SyntaxNode Root)> FixtureAsync()
    {
        var solution = TestSolutions.Create(("Holder.cs", Source));
        var document = solution.Projects.Single().Documents.Single();

        return (solution,
            await document.GetSemanticModelAsync(TestContext.Current.CancellationToken),
            await document.GetSyntaxRootAsync(TestContext.Current.CancellationToken));
    }

    private static Task<AnalyzerResult> RunAsync(ReferenceAnalyzerKind analyzer, ISymbol symbol, Solution solution) =>
        ReferenceGraphEngine.RunAsync(analyzer, symbol, solution, null, null, TestContext.Current.CancellationToken);

    private static ISymbol Running(SemanticModel model, SyntaxNode root) =>
        model.GetDeclaredSymbol(root.DescendantNodes().OfType<VariableDeclaratorSyntax>().First(v => v.Identifier.Text == "running"), TestContext.Current.CancellationToken);

    [Fact]
    public async Task ALocal_SplitsItsWritesFromItsReads()
    {
        var (solution, model, root) = await FixtureAsync();
        var running = Running(model, root);

        var assignedBy = Assert.Single((await RunAsync(ReferenceAnalyzerKind.AssignedBy, running, solution)).Rows);
        var readBy = Assert.Single((await RunAsync(ReferenceAnalyzerKind.ReadBy, running, solution)).Rows);

        Assert.Equal(PickLabel, assignedBy.DisplayText);
        Assert.Single(assignedBy.Locations);
        Assert.Equal(PickLabel, readBy.DisplayText);
        Assert.Equal(4, readBy.Locations.Count);
    }

    /// <summary>A local function becomes a row under Uses, but an occurrence inside one still belongs to the member around it.</summary>
    [Fact]
    public async Task AnOccurrenceInsideALocalFunction_IsAttributedToTheMemberAroundIt()
    {
        var (solution, model, root) = await FixtureAsync();

        var rows = (await RunAsync(ReferenceAnalyzerKind.ReadBy, Running(model, root), solution)).Rows;

        Assert.All(rows, r => Assert.Equal(PickLabel, r.DisplayText));
    }

    [Fact]
    public async Task ACallToALocalFunction_IsARowUnderUsesThatExpands()
    {
        var (solution, _, _) = await FixtureAsync();
        var pick = (await solution.Projects.Single().GetCompilationAsync(TestContext.Current.CancellationToken))
            .GetTypeByMetadataName("N.Holder`1").GetMembers("Pick").Single();

        var row = Assert.Single((await RunAsync(ReferenceAnalyzerKind.Uses, pick, solution)).Rows, r => r.Glyph == SymbolGlyph.LocalFunction);
        var resolved = await row.Identity.ResolveAsync(solution, TestContext.Current.CancellationToken);

        Assert.True(row.Identity.IsPositional);
        Assert.Equal(MethodKind.LocalFunction, Assert.IsAssignableFrom<IMethodSymbol>(resolved).MethodKind);
        Assert.NotNull((await RunAsync(ReferenceAnalyzerKind.Uses, resolved, solution)).Rows);
    }

    [Fact]
    public async Task ALambda_UsesWalksItsBody()
    {
        var (solution, model, root) = await FixtureAsync();
        var lambda = model.GetSymbolInfo(root.DescendantNodes().OfType<SimpleLambdaExpressionSyntax>().Single(), TestContext.Current.CancellationToken).Symbol;

        var rows = (await RunAsync(ReferenceAnalyzerKind.Uses, lambda, solution)).Rows;

        Assert.Contains(rows, r => r.DisplayText == "Holder<TItem>.Twice(int)");
        Assert.Contains(rows, r => r.Glyph == SymbolGlyph.LocalFunction);
    }

    /// <summary>Every occurrence of a type parameter is a type reference, which the ordinary Used By mask excludes.</summary>
    [Fact]
    public async Task ATypeParameter_UsedByFindsTheMembersNamingIt()
    {
        var (solution, model, root) = await FixtureAsync();
        var typeParameter = model.GetDeclaredSymbol(root.DescendantNodes().OfType<TypeParameterSyntax>().Single(), TestContext.Current.CancellationToken);

        var labels = (await RunAsync(ReferenceAnalyzerKind.UsedBy, typeParameter, solution)).Rows.Select(r => r.DisplayText).ToList();

        Assert.Contains("Holder<TItem>.Keep", labels);
        Assert.Contains("Holder<TItem>.Echo(TItem)", labels);
    }

    [Fact]
    public async Task EveryNewRootKind_IsASupportedRoot()
    {
        var (_, model, root) = await FixtureAsync();
        var cancellationToken = TestContext.Current.CancellationToken;

        var roots = new ISymbol[]
        {
            Running(model, root),
            model.GetDeclaredSymbol(root.DescendantNodes().OfType<ParameterSyntax>().First(p => p.Identifier.Text == "count"), cancellationToken),
            model.GetDeclaredSymbol(root.DescendantNodes().OfType<TypeParameterSyntax>().Single(), cancellationToken),
            model.GetDeclaredSymbol(root.DescendantNodes().OfType<LocalFunctionStatementSyntax>().Single(), cancellationToken),
            model.GetSymbolInfo(root.DescendantNodes().OfType<SimpleLambdaExpressionSyntax>().Single(), cancellationToken).Symbol
        };

        Assert.All(roots, s => Assert.True(SymbolResolver.IsSupportedRoot(s), s.Kind + " " + s.Name));
    }
}
