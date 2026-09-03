using System.Linq;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

using RoslynQuery.Mcp.Contracts;
using RoslynQuery.Query;

using Xunit;

namespace RoslynQuery.Tests;

// Predicates are emitted as `async ValueTask<object>`, so a predicate may await. These cover the
// capability itself: that awaiting compiles, that a genuinely asynchronous predicate still yields
// the right answer, and that the ordinary non-awaiting predicate stays synchronous.
[Collection(PredicateCompilerCacheCollection.Name)]
public class PredicateAwaitTests
{
    private static async Task<(SyntaxNode node, SemanticModel model, Document doc)> LocalDeclarationAsync()
    {
        var workspace = new AdhocWorkspace();
        var projectInfo = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Create(),
            "AwaitTestProject",
            "AwaitTestProject",
            LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);

        var project = workspace.AddProject(projectInfo);
        var document = workspace.AddDocument(project.Id, "Test.cs", SourceText.From("class C { void M() { object x = null; } }"));

        var token = TestContext.Current.CancellationToken;
        var model = await document.GetSemanticModelAsync(token);
        var root = await document.GetSyntaxRootAsync(token);

        return (root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>().First(), model, document);
    }

    [Fact]
    public async Task Compile_ExpressionAwaitingARoslynApi_Runs()
    {
        var (node, model, doc) = await LocalDeclarationAsync();

        var match = (NodeMatch)PredicateCompiler.Compile(TargetKind.SyntaxNode, "(await doc.GetSyntaxRootAsync()) != null");

        Assert.Equal(true, await match(node, model, doc));
    }

    [Fact]
    public async Task Compile_BodyAwaitingARoslynApi_Runs()
    {
        var (node, model, doc) = await LocalDeclarationAsync();

        var match = (NodeMatch)PredicateCompiler.Compile(
            TargetKind.SyntaxNode,
            "var root = await doc.GetSyntaxRootAsync();\r\nreturn root.DescendantNodes().Any();");

        Assert.Equal(true, await match(node, model, doc));
    }

    [Fact]
    public async Task Compile_PredicateThatActuallyYields_StillReturnsTheRightAnswer()
    {
        var (node, model, doc) = await LocalDeclarationAsync();

        // Task.Yield forces the continuation off the synchronous path, so this only passes if the
        // await is genuinely plumbed through rather than the result being read eagerly.
        var yes = (NodeMatch)PredicateCompiler.Compile(TargetKind.SyntaxNode, "await Task.Yield();\r\nreturn n != null;");
        var no = (NodeMatch)PredicateCompiler.Compile(TargetKind.SyntaxNode, "await Task.Yield();\r\nreturn n == null;");

        Assert.Equal(true, await yes(node, model, doc));
        Assert.Equal(false, await no(node, model, doc));
    }

    [Fact]
    public async Task Compile_PredicateThatNeverAwaits_CompletesSynchronously()
    {
        var (node, model, doc) = await LocalDeclarationAsync();

        var match = (NodeMatch)PredicateCompiler.Compile(TargetKind.SyntaxNode, "n != null");

        // The reason the delegates return ValueTask and not Task: the overwhelmingly common
        // non-awaiting predicate has to stay allocation-free across a run's hundreds of thousands
        // of invocations.
        var pending = match(node, model, doc);

        Assert.True(pending.IsCompletedSuccessfully);
        Assert.Equal(true, await pending);
    }

    [Fact]
    public async Task Compile_AwaitingPredicate_ThatThrows_SurfacesTheException()
    {
        var (node, model, doc) = await LocalDeclarationAsync();

        // The engine catches per-invocation faults and counts them as predicate errors; that only
        // works if the fault arrives through the awaited ValueTask rather than being swallowed.
        var match = (NodeMatch)PredicateCompiler.Compile(
            TargetKind.SyntaxNode,
            "await Task.Yield();\r\nthrow new System.InvalidOperationException(\"boom\");");

        var ex = await Assert.ThrowsAsync<System.InvalidOperationException>(async () => await match(node, model, doc));
        Assert.Equal("boom", ex.Message);
    }
}
