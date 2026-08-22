using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

using RoslynQuery.Query;
using RoslynQuery.Replace;

using Xunit;

namespace RoslynQuery.Tests;

// ReplaceEngine invokes ReplaceCompiler, whose cache is the same process-wide static as
// PredicateCompiler's - see PredicateCompilerCacheCollection.
[Collection(PredicateCompilerCacheCollection.Name)]
public class ReplaceEngineTests
{
    private static async Task<(Document Document, SyntaxNode Root, SourceText Text)> DocumentAsync(string source)
    {
        var workspace = new AdhocWorkspace();
        var projectInfo = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Create(),
            "ReplaceEngineTestProject",
            "ReplaceEngineTestProject",
            LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);

        var project = workspace.AddProject(projectInfo);
        var document = workspace.AddDocument(project.Id, "Test.cs", SourceText.From(source));

        var token = TestContext.Current.CancellationToken;
        var root = await document.GetSyntaxRootAsync(token);
        var text = await document.GetTextAsync(token);
        return (document, root, text);
    }

    [Fact]
    public async Task GenerateAsync_NodeTarget_ProducesReplacementText()
    {
        var (doc, root, text) = await DocumentAsync("class C { void M() { int x = 1; } }");
        var local = root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>().First();
        var hit = QueryHit.Create(doc, text, local.Span, local.Kind().ToString(), TargetKind.SyntaxNode);

        var replace = ReplaceCompiler.Compile(TargetKind.SyntaxNode, "\"// replaced\"");
        var items = await ReplaceEngine.GenerateAsync(doc.Project.Solution, [hit], TargetKind.SyntaxNode, replace, TestContext.Current.CancellationToken);

        var item = Assert.Single(items);
        Assert.True(item.Included);
        Assert.Equal("// replaced", item.After);
        Assert.Null(item.Warning);
    }

    [Fact]
    public async Task GenerateAsync_TokenTarget_ProducesReplacementText()
    {
        var (doc, root, text) = await DocumentAsync("class C { void M() { int x = 1; } }");
        var identifier = root.DescendantTokens().First(t => t.IsKind(SyntaxKind.IdentifierToken) && t.Text == "x");
        var hit = QueryHit.Create(doc, text, identifier.Span, identifier.Kind().ToString(), TargetKind.SyntaxToken);

        var replace = ReplaceCompiler.Compile(TargetKind.SyntaxToken, "t.Text.ToUpperInvariant()");
        var items = await ReplaceEngine.GenerateAsync(doc.Project.Solution, [hit], TargetKind.SyntaxToken, replace, TestContext.Current.CancellationToken);

        var item = Assert.Single(items);
        Assert.True(item.Included);
        Assert.Equal("X", item.After);
    }

    [Fact]
    public async Task GenerateAsync_NodeReplacementResult_IsNormalizedAndStringified()
    {
        var (doc, root, text) = await DocumentAsync("class C { void M() { int x = 1; } }");
        var local = root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>().First();
        var hit = QueryHit.Create(doc, text, local.Span, local.Kind().ToString(), TargetKind.SyntaxNode);

        // Returns the same node it was given, so After should just be its own normalized form.
        var replace = ReplaceCompiler.Compile(TargetKind.SyntaxNode, "n");
        var items = await ReplaceEngine.GenerateAsync(doc.Project.Solution, [hit], TargetKind.SyntaxNode, replace, TestContext.Current.CancellationToken);

        var item = Assert.Single(items);
        Assert.True(item.Included);
        Assert.Equal(local.NormalizeWhitespace().ToFullString(), item.After);
    }

    [Fact]
    public async Task GenerateAsync_NullResult_SkipsWithWarning()
    {
        var (doc, root, text) = await DocumentAsync("class C { void M() { int x = 1; } }");
        var local = root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>().First();
        var hit = QueryHit.Create(doc, text, local.Span, local.Kind().ToString(), TargetKind.SyntaxNode);

        var replace = ReplaceCompiler.Compile(TargetKind.SyntaxNode, PredicateMode.Body, "return null;");
        var items = await ReplaceEngine.GenerateAsync(doc.Project.Solution, [hit], TargetKind.SyntaxNode, replace, TestContext.Current.CancellationToken);

        var item = Assert.Single(items);
        Assert.False(item.Included);
        Assert.NotNull(item.Warning);
    }

    [Fact]
    public async Task GenerateAsync_HitAtAStaleSpan_SkipsWithWarning()
    {
        var (doc, root, text) = await DocumentAsync("class C { void M() { int x = 1; } }");
        // A (span, kind) pair that never lined up with any node in this tree.
        var hit = QueryHit.Create(doc, text, new TextSpan(0, 3), "BogusKind", TargetKind.SyntaxNode);

        var replace = ReplaceCompiler.Compile(TargetKind.SyntaxNode, "\"x\"");
        var items = await ReplaceEngine.GenerateAsync(doc.Project.Solution, [hit], TargetKind.SyntaxNode, replace, TestContext.Current.CancellationToken);

        var item = Assert.Single(items);
        Assert.False(item.Included);
        Assert.Contains("no longer exists", item.Warning);
    }

    [Fact]
    public async Task MarkConflicts_OverlappingHitsInSameDocument_UnchecksTheLaterOne()
    {
        var (doc, root, text) = await DocumentAsync("class C { void M() { int x = 1; } }");
        var local = root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>().First();
        var declarator = local.DescendantNodes().OfType<VariableDeclaratorSyntax>().First();

        var outer = QueryHit.Create(doc, text, local.Span, local.Kind().ToString(), TargetKind.SyntaxNode);
        var inner = QueryHit.Create(doc, text, declarator.Span, declarator.Kind().ToString(), TargetKind.SyntaxNode);

        var items = new List<ReplacementItem>
        {
            new ReplacementItem { Hit = outer, Before = outer.Preview, After = "a" },
            new ReplacementItem { Hit = inner, Before = inner.Preview, After = "b" },
        };

        ReplaceEngine.MarkConflicts(items);

        Assert.Single(items, i => i.Included);
        Assert.Contains(items, i => !i.Included && i.Warning != null);
    }

    [Fact]
    public async Task MarkConflicts_NonOverlappingHits_BothStayIncluded()
    {
        var (doc, root, text) = await DocumentAsync("class C { void M() { int x = 1; int y = 2; } }");
        var locals = root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>().ToArray();

        var items = locals.Select(l => new ReplacementItem
        {
            Hit = QueryHit.Create(doc, text, l.Span, l.Kind().ToString(), TargetKind.SyntaxNode),
            Before = "before",
            After = "after",
        }).ToList();

        ReplaceEngine.MarkConflicts(items);

        Assert.All(items, i => Assert.True(i.Included));
    }
}
