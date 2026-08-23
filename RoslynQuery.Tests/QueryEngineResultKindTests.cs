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
        QueryEngine.RunAsync(
            [unit], target, expression, PredicateCompiler.Compile(target, expression), maxResults: 100,
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
