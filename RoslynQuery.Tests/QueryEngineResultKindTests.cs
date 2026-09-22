using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

using RoslynQuery.Query;

using Xunit;

namespace RoslynQuery.Tests;

// QueryEngine invokes PredicateCompiler, whose cache is the same process-wide static shared with
// ReplaceCompiler - see PredicateCompilerCacheCollection.
[Collection(PredicateCompilerCacheCollection.Name)]
public class QueryEngineResultKindTests
{
    private static async Task<ScopeUnit> UnitAsync(string source)
    {
        var workspace = new AdhocWorkspace();
        var projectInfo = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Create(),
            "QueryEngineResultKindTestProject",
            "QueryEngineResultKindTestProject",
            LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);

        var project = workspace.AddProject(projectInfo);
        var document = workspace.AddDocument(project.Id, "Test.cs", SourceText.From(source));

        // Force the tree/model to exist before the scan runs; irrelevant to what is asserted below.
        await document.GetSyntaxRootAsync(TestContext.Current.CancellationToken);

        return new ScopeUnit(document, null, filterGenerated: false);
    }

    private static Task<QueryOutcome> RunAsync(ScopeUnit unit, TargetKind target, string expression, ICollection<QueryHit> hits) =>
        RunAsync([unit], target, expression, hits, maxResults: 100);

    private static Task<QueryOutcome> RunAsync(
        IReadOnlyList<ScopeUnit> units, TargetKind target, string expression, ICollection<QueryHit> hits, int maxResults) =>
        QueryEngine.RunAsync(
            units, target, expression, PredicateCompiler.Compile(target, expression), maxResults,
            onBatch: batch => { foreach (var hit in batch) hits.Add(hit); },
            CancellationToken.None);

    [Fact]
    public async Task RunAsync_NodePredicateReturnsTrue_MatchesAsBefore()
    {
        var unit = await UnitAsync("class C { void M() { if (true) { } } }");
        var hits = new List<QueryHit>();

        var outcome = await RunAsync(unit, TargetKind.SyntaxNode, "n.IsKind(SyntaxKind.IfStatement)", hits);

        Assert.Equal(1, outcome.Matched);
        Assert.Equal(0, outcome.Errors);
        Assert.Equal("IfStatement", Assert.Single(hits).Kind);
    }

    [Fact]
    public async Task RunAsync_NodePredicateReturnsNull_ActsAsNoMatch()
    {
        var unit = await UnitAsync("class C { void M() { if (true) { } } }");
        var hits = new List<QueryHit>();

        var body = "if (n.IsKind(SyntaxKind.IfStatement)) return null;\r\nreturn false;";
        var outcome = await RunAsync(unit, TargetKind.SyntaxNode, body, hits);

        Assert.Equal(0, outcome.Matched);
        Assert.Equal(0, outcome.Errors);
        Assert.Empty(hits);
    }

    [Fact]
    public async Task RunAsync_NodePredicateReturnsAnAncestorNode_HitUsesThatNodesSpanAndKind()
    {
        var unit = await UnitAsync("class C { void M() { if (true) { } } }");
        var hits = new List<QueryHit>();

        // A Where+Select in one call: match the if-statement, but report its containing method.
        var body = "if (n.IsKind(SyntaxKind.IfStatement)) return n.FirstAncestorOrSelf<MethodDeclarationSyntax>();\r\nreturn false;";
        var outcome = await RunAsync(unit, TargetKind.SyntaxNode, body, hits);

        Assert.Equal(1, outcome.Matched);
        Assert.Equal(0, outcome.Errors);
        var hit = Assert.Single(hits);
        Assert.Equal("MethodDeclaration", hit.Kind);
    }

    [Fact]
    public async Task RunAsync_MultipleMatchesReportingTheSameAncestor_CollapseToOneHit()
    {
        var unit = await UnitAsync("class C { void M() { int a = 1; int b = 2; int c = 3; } }");
        var hits = new List<QueryHit>();

        // Three distinct local declarations all report the one method they live in - without
        // deduplication this would show up as three identical rows for that one location.
        var body = "if (n.IsKind(SyntaxKind.LocalDeclarationStatement)) return n.FirstAncestorOrSelf<MethodDeclarationSyntax>();\r\nreturn false;";
        var outcome = await RunAsync(unit, TargetKind.SyntaxNode, body, hits);

        Assert.Equal(1, outcome.Matched);
        Assert.Equal(0, outcome.Errors);
        Assert.Equal("MethodDeclaration", Assert.Single(hits).Kind);
    }

    [Fact]
    public async Task RunAsync_MultipleTokenMatchesReportingTheSameToken_CollapseToOneHit()
    {
        var unit = await UnitAsync("class C { void M() { int a = 1; int b = 2; } }");
        const string selector = "t.IsKind(SyntaxKind.IdentifierToken) && (t.Text == \"a\" || t.Text == \"b\")";

        // Baseline: without redirecting the result there are two separate matches left to collapse.
        var baseline = new List<QueryHit>();
        Assert.Equal(2, (await RunAsync(unit, TargetKind.SyntaxToken, selector, baseline)).Matched);

        var hits = new List<QueryHit>();

        // Both declared names report the method's own identifier token instead of themselves.
        var body = "if (" + selector + ")\r\n"
            + "    return t.Parent.FirstAncestorOrSelf<MethodDeclarationSyntax>().Identifier;\r\nreturn false;";
        var outcome = await RunAsync(unit, TargetKind.SyntaxToken, body, hits);

        Assert.Equal(1, outcome.Matched);
        Assert.Equal(0, outcome.Errors);
        Assert.Equal("IdentifierToken", Assert.Single(hits).Kind);
    }

    [Fact]
    public async Task RunAsync_MultipleOperationMatchesReportingTheSameOperation_CollapseToOneHit()
    {
        var unit = await UnitAsync("class C { void M() { int a = 1; int b = 2; } }");

        // Baseline: without redirecting the result there are two separate matches left to collapse.
        var baseline = new List<QueryHit>();
        Assert.Equal(2, (await RunAsync(unit, TargetKind.Operation, "op is ILiteralOperation", baseline)).Matched);

        var hits = new List<QueryHit>();

        // Every literal reports the root of its operation tree, which is the one method body.
        var body = "if (op is not ILiteralOperation) return false;\r\n"
            + "var root = op;\r\nwhile (root.Parent != null) root = root.Parent;\r\nreturn root;";
        var outcome = await RunAsync(unit, TargetKind.Operation, body, hits);

        Assert.Equal(1, outcome.Matched);
        Assert.Equal(0, outcome.Errors);
        Assert.Single(hits);
    }

    /// <summary>The key is (file, span, kind); keyed on kind alone this would collapse to one row.</summary>
    [Fact]
    public async Task RunAsync_SameKindAtDifferentSpans_StaySeparateHits()
    {
        var unit = await UnitAsync("class C { void M() { int a = 1; } void N() { int b = 2; } }");
        var hits = new List<QueryHit>();

        var body = "if (n.IsKind(SyntaxKind.LocalDeclarationStatement)) return n.FirstAncestorOrSelf<MethodDeclarationSyntax>();\r\nreturn false;";
        var outcome = await RunAsync(unit, TargetKind.SyntaxNode, body, hits);

        Assert.Equal(2, outcome.Matched);
        Assert.Equal(0, outcome.Errors);
        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.Equal("MethodDeclaration", h.Kind));
        Assert.Equal(2, hits.Select(h => h.Span).Distinct().Count());
    }

    /// <summary>Two files can hold the identical span and kind, so the file has to be part of the key.</summary>
    [Fact]
    public async Task RunAsync_SameSpanAndKindInDifferentDocuments_StaySeparateHits()
    {
        const string source = "class C { void M() { int a = 1; } }";
        var units = new[] { await UnitAsync(source), await UnitAsync(source) };
        var hits = new List<QueryHit>();

        var body = "if (n.IsKind(SyntaxKind.LocalDeclarationStatement)) return n.FirstAncestorOrSelf<MethodDeclarationSyntax>();\r\nreturn false;";
        var outcome = await RunAsync(units, TargetKind.SyntaxNode, body, hits, maxResults: 100);

        Assert.Equal(2, outcome.Matched);
        Assert.Equal(2, hits.Count);
        Assert.Equal(hits[0].Span, hits[1].Span);
        Assert.Equal(2, hits.Select(h => h.DocumentId).Distinct().Count());
    }

    /// <summary>Dedupe runs before the cap counter, so collapsed duplicates must not spend the budget.</summary>
    [Fact]
    public async Task RunAsync_CollapsedDuplicates_DoNotCountAgainstTheCap()
    {
        var unit = await UnitAsync("class C { void M() { int a = 1; int b = 2; int c = 3; } }");
        var hits = new List<QueryHit>();

        var body = "if (n.IsKind(SyntaxKind.LocalDeclarationStatement)) return n.FirstAncestorOrSelf<MethodDeclarationSyntax>();\r\nreturn false;";
        var outcome = await RunAsync([unit], TargetKind.SyntaxNode, body, hits, maxResults: 1);

        Assert.Equal(1, outcome.Matched);
        Assert.False(outcome.Truncated);
        Assert.Single(hits);
    }

    [Fact]
    public async Task RunAsync_TokenPredicateReturnsAnotherToken_HitUsesThatTokensSpanAndKind()
    {
        var unit = await UnitAsync("class C { void M() { int x = 1; } }");
        var hits = new List<QueryHit>();

        // Match the "x" identifier token, but report the ";" that ends its statement instead.
        var body = "if (t.IsKind(SyntaxKind.IdentifierToken) && t.Text == \"x\") return t.Parent.Parent.Parent.GetLastToken();\r\nreturn false;";
        var outcome = await RunAsync(unit, TargetKind.SyntaxToken, body, hits);

        Assert.Equal(1, outcome.Matched);
        Assert.Equal(0, outcome.Errors);
        Assert.Equal("SemicolonToken", Assert.Single(hits).Kind);
    }

    [Fact]
    public async Task RunAsync_NodePredicateReturnsANodeBuiltWithSyntaxFactory_CountsAsAnErrorNotAHit()
    {
        var unit = await UnitAsync("class C { void M() { if (true) { } } }");
        var hits = new List<QueryHit>();

        // Not part of the tree being searched - must be rejected rather than silently accepted.
        var body = "if (n.IsKind(SyntaxKind.IfStatement)) return SyntaxFactory.IdentifierName(\"bogus\");\r\nreturn false;";
        var outcome = await RunAsync(unit, TargetKind.SyntaxNode, body, hits);

        Assert.Equal(0, outcome.Matched);
        Assert.Equal(1, outcome.Errors);
        Assert.Empty(hits);
    }

    [Fact]
    public async Task RunAsync_NodePredicateReturnsAnUnsupportedType_CountsAsAnError()
    {
        var unit = await UnitAsync("class C { void M() { if (true) { } } }");
        var hits = new List<QueryHit>();

        var body = "if (n.IsKind(SyntaxKind.IfStatement)) return \"oops\";\r\nreturn false;";
        var outcome = await RunAsync(unit, TargetKind.SyntaxNode, body, hits);

        Assert.Equal(0, outcome.Matched);
        Assert.Equal(1, outcome.Errors);
        Assert.Empty(hits);
    }
}
