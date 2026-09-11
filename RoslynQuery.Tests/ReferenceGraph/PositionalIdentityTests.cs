using System.Linq;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

using RoslynQuery.ReferenceGraph;

using Xunit;

namespace RoslynQuery.Tests;

// Locals, parameters, type parameters, local functions and lambdas have no usable documentation comment id,
// so they are identified by the file and span of their declaration.
public class PositionalIdentityTests
{
    private const string Source = """
        using System;

        namespace N
        {
            public class Holder<TItem>
            {
                public int Pick<TOther>(int count)
                {
                    var running = count;

                    int Helper(int seed) => seed + running;

                    Func<int, int> first = x => x;
                    Func<int, int> second = y => y + 1;

                    return Helper(running);
                }

                public void SiblingBlocks()
                {
                    { int F() => 1; F(); }
                    { int F() => 2; F(); }
                }

                public void Other()
                {
                    var running = 5;
                }
            }
        }
        """;

    private static async Task<(Solution Solution, SemanticModel Model, SyntaxNode Root)> FixtureAsync()
    {
        var solution = TestSolutions.Create(("Holder.cs", Source));
        var document = solution.Projects.Single().Documents.Single();

        return (solution,
            await document.GetSemanticModelAsync(TestContext.Current.CancellationToken),
            await document.GetSyntaxRootAsync(TestContext.Current.CancellationToken));
    }

    private static ISymbol Local(SemanticModel model, SyntaxNode root, string name, int index = 0) =>
        model.GetDeclaredSymbol(root.DescendantNodes().OfType<VariableDeclaratorSyntax>().Where(v => v.Identifier.Text == name).ElementAt(index), TestContext.Current.CancellationToken);

    private static ISymbol Lambda(SemanticModel model, SyntaxNode root, int index) =>
        model.GetSymbolInfo(root.DescendantNodes().OfType<SimpleLambdaExpressionSyntax>().ElementAt(index), TestContext.Current.CancellationToken).Symbol;

    private static ISymbol LocalFunction(SemanticModel model, SyntaxNode root, string name, int index = 0) =>
        model.GetDeclaredSymbol(root.DescendantNodes().OfType<LocalFunctionStatementSyntax>().Where(f => f.Identifier.Text == name).ElementAt(index), TestContext.Current.CancellationToken);

    private static SymbolIdentity Identity(ISymbol symbol, Solution solution) =>
        SymbolIdentity.Create(symbol, solution, solution.ProjectIds.Single());

    private static async Task AssertRoundTripsAsync(ISymbol symbol, Solution solution)
    {
        var identity = Identity(symbol, solution);

        Assert.True(identity.IsPositional);
        Assert.False(identity.IsEmpty);
        Assert.True(SymbolEqualityComparer.Default.Equals(symbol, await identity.ResolveAsync(solution, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task ALocal_RoundTrips()
    {
        var (solution, model, root) = await FixtureAsync();

        await AssertRoundTripsAsync(Local(model, root, "running"), solution);
    }

    [Fact]
    public async Task AParameter_RoundTrips()
    {
        var (solution, model, root) = await FixtureAsync();

        await AssertRoundTripsAsync(
            model.GetDeclaredSymbol(root.DescendantNodes().OfType<ParameterSyntax>().First(p => p.Identifier.Text == "count"), TestContext.Current.CancellationToken),
            solution);
    }

    [Fact]
    public async Task TypeParametersOfATypeAndOfAMethod_RoundTrip()
    {
        var (solution, model, root) = await FixtureAsync();

        foreach (var typeParameter in root.DescendantNodes().OfType<TypeParameterSyntax>())
            await AssertRoundTripsAsync(model.GetDeclaredSymbol(typeParameter, TestContext.Current.CancellationToken), solution);
    }

    [Fact]
    public async Task ALocalFunction_RoundTrips()
    {
        var (solution, model, root) = await FixtureAsync();

        await AssertRoundTripsAsync(LocalFunction(model, root, "Helper"), solution);
    }

    [Fact]
    public async Task ALambda_RoundTrips()
    {
        var (solution, model, root) = await FixtureAsync();

        await AssertRoundTripsAsync(Lambda(model, root, 0), solution);
    }

    /// <summary>A documentation id gives both the same string; position tells them apart.</summary>
    [Fact]
    public async Task SameShapeLambdas_AreDifferentIdentities()
    {
        var (solution, model, root) = await FixtureAsync();

        Assert.NotEqual(Identity(Lambda(model, root, 0), solution), Identity(Lambda(model, root, 1), solution));
    }

    [Fact]
    public async Task SameNamedSiblingLocalFunctions_AreDifferentIdentities()
    {
        var (solution, model, root) = await FixtureAsync();

        Assert.NotEqual(Identity(LocalFunction(model, root, "F", 0), solution), Identity(LocalFunction(model, root, "F", 1), solution));
    }

    [Fact]
    public async Task SameNamedLocalsInDifferentMethods_AreDifferentIdentities()
    {
        var (solution, model, root) = await FixtureAsync();

        Assert.NotEqual(Identity(Local(model, root, "running", 0), solution), Identity(Local(model, root, "running", 1), solution));
    }

    [Fact]
    public async Task APositionalIdentity_CannotBeMistakenForADocumentationId()
    {
        var (solution, model, root) = await FixtureAsync();

        Assert.StartsWith("@:", Identity(Local(model, root, "running"), solution).DeclarationId);
    }

    /// <summary>A node cannot keep the snapshot its span came from, so an edit above the declaration makes the identity stale rather than landing on a neighbour.</summary>
    [Fact]
    public async Task AfterAnEditAboveTheDeclaration_ResolutionReturnsNullRatherThanANeighbour()
    {
        var (solution, model, root) = await FixtureAsync();
        var identity = Identity(Local(model, root, "running"), solution);

        var edited = solution.WithDocumentText(solution.Projects.Single().DocumentIds.Single(), SourceText.From("// shifted\n" + Source));

        Assert.Null(await identity.ResolveAsync(edited, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InAMultiTargetedProject_EachCopyOfTheFileGivesTheSameIdentity()
    {
        var solution = TestSolutions.MultiTargeted(2, "Holder.cs", Source);
        var identities = new SymbolIdentity[2];

        for (var i = 0; i < 2; i++)
        {
            var document = solution.Projects.ElementAt(i).Documents.Single();
            var model = await document.GetSemanticModelAsync(TestContext.Current.CancellationToken);
            var root = await document.GetSyntaxRootAsync(TestContext.Current.CancellationToken);

            identities[i] = SymbolIdentity.Create(Local(model, root, "running"), solution, document.Project.Id);
        }

        Assert.Equal(identities[0], identities[1]);
    }
}
