using System;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

using RoslynQuery.Mcp.Contracts;
using RoslynQuery.Query;
using RoslynQuery.Replace;

using Xunit;

namespace RoslynQuery.Tests;

// ReplaceCompiler's cache is a process-wide static too (mirrors PredicateCompiler's), so this
// shares the same serialized collection as the predicate-compiling tests.
[Collection(PredicateCompilerCacheCollection.Name)]
public class ReplaceCompilerTests
{
    private static async Task<(SyntaxNode node, SemanticModel model, Document doc)> LocalDeclarationAsync()
    {
        var workspace = new AdhocWorkspace();
        var projectInfo = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Create(),
            "ReplaceTestProject",
            "ReplaceTestProject",
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
    public async Task Compile_StringExpression_ReturnsTheString()
    {
        var (node, model, doc) = await LocalDeclarationAsync();

        var replace = (NodeReplace)ReplaceCompiler.Compile(TargetKind.SyntaxNode, "\"replaced\"");
        var result = await replace(node, model, doc);

        Assert.Equal("replaced", Assert.IsType<string>(result));
    }

    [Fact]
    public async Task Compile_NodeExpression_ReturnsTheSyntaxNode()
    {
        var (node, model, doc) = await LocalDeclarationAsync();

        var replace = (NodeReplace)ReplaceCompiler.Compile(TargetKind.SyntaxNode, "n");
        var result = await replace(node, model, doc);

        Assert.Same(node, Assert.IsAssignableFrom<SyntaxNode>(result));
    }

    [Fact]
    public async Task Compile_BodyModeReturningNull_SkipsTheHit()
    {
        var (node, model, doc) = await LocalDeclarationAsync();

        var replace = (NodeReplace)ReplaceCompiler.Compile(TargetKind.SyntaxNode, PredicateMode.Body, "return null;");
        var result = await replace(node, model, doc);

        Assert.Null(result);
    }

    [Fact]
    public async Task Compile_TokenExpression_ReturnsTheReplacementString()
    {
        var (node, model, doc) = await LocalDeclarationAsync();
        var token = node.DescendantTokens().First(t => t.IsKind(SyntaxKind.IdentifierToken));

        var replace = (TokenReplace)ReplaceCompiler.Compile(TargetKind.SyntaxToken, "t.Text.ToUpperInvariant()");
        var result = await replace(token, model, doc);

        Assert.Equal(token.Text.ToUpperInvariant(), Assert.IsType<string>(result));
    }

    [Fact]
    public void Compile_Operation_Throws() =>
        Assert.Throws<NotSupportedException>(() => ReplaceCompiler.Compile(TargetKind.Operation, "op"));

    [Fact]
    public void Compile_DirectiveBearingInput_IsRejected()
    {
        var ex = Assert.Throws<PredicateCompilationException>(
            () => ReplaceCompiler.Compile(TargetKind.SyntaxNode, "#if DEBUG\r\n\"a\"\r\n#else\r\n\"b\"\r\n#endif"));

        Assert.Contains("#if DEBUG", ex.Message);
    }

    [Fact]
    public void Compile_SameTextAcrossCalls_ReturnsCachedDelegate()
    {
        var first = ReplaceCompiler.Compile(TargetKind.SyntaxNode, "\"same text\"");
        var second = ReplaceCompiler.Compile(TargetKind.SyntaxNode, "\"same text\"");

        Assert.Same(first, second);
    }

    [Fact]
    public void DelegateType_ReturnsExpected()
    {
        Assert.Equal(typeof(NodeReplace), ReplaceCompiler.DelegateType(TargetKind.SyntaxNode));
        Assert.Equal(typeof(TokenReplace), ReplaceCompiler.DelegateType(TargetKind.SyntaxToken));
        Assert.Throws<NotSupportedException>(() => ReplaceCompiler.DelegateType(TargetKind.Operation));
    }
}
