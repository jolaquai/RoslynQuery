using System;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

using RoslynQuery.Query;

using Xunit;

namespace RoslynQuery.Tests;

public class PredicateModeDetectionTests
{
    [Theory]
    [InlineData("n != null")]
    [InlineData("true")]
    [InlineData("n is object && n.Parent != null")]
    [InlineData("n.ToString() == \"return\"")]
    // A lambda block body carries a return while the whole thing is still one expression, which is
    // exactly what a "contains a return token" test gets wrong.
    [InlineData("n.ChildNodes().Any(c => { return c != null; })")]
    [InlineData("n.DescendantNodes().Where(x => { if (x == null) return false; return true; }).Any()")]
    public void DetectMode_Expressions(string text) =>
        Assert.Equal(PredicateMode.Expression, PredicateCompiler.DetectMode(text));

    [Theory]
    [InlineData("return true;")]
    [InlineData("var x = 1; return x > 0;")]
    [InlineData("if (n == null) return false; return true;")]
    [InlineData("foreach (var c in n.ChildNodes()) { if (c != null) return true; } return false;")]
    [InlineData("int i = 0; while (i < 10) i++; return i == 10;")]
    [InlineData("var t = n.ToString();\r\nreturn t.Length > 0;")]
    // No return token anywhere, but still a body.
    [InlineData("throw new Exception();")]
    public void DetectMode_Bodies(string text) =>
        Assert.Equal(PredicateMode.Body, PredicateCompiler.DetectMode(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DetectMode_Empty_IsExpression(string text) =>
        Assert.Equal(PredicateMode.Expression, PredicateCompiler.DetectMode(text));
}

// CompletionMode exists because DetectMode answers "not a complete expression" with Body, and text
// being typed is never a complete expression - so scaffolding completions off DetectMode put every
// keystroke of an expression predicate in a statement body, and committing an item then ate the
// character in front of the word.
public class PredicateCompletionModeTests
{
    [Theory]
    // The regression case: each of these is a prefix of "n.IsKind(SyntaxKind.IfStatement)" as it is
    // typed, and DetectMode calls every one of them a body.
    [InlineData("n.IsKind(SyntaxKind")]
    [InlineData("n.IsKind(SyntaxKind.")]
    [InlineData("n.IsKind(SyntaxKind.If")]
    [InlineData("n.")]
    [InlineData("n.Parent.")]
    [InlineData("n is object &&")]
    public void CompletionMode_PartiallyTypedExpression_IsExpression(string text) =>
        Assert.Equal(PredicateMode.Expression, PredicateCompiler.CompletionMode(text));

    [Theory]
    [InlineData("n != null")]
    [InlineData("n.IsKind(SyntaxKind.IfStatement)")]
    [InlineData("n.ChildNodes().Any(c => { return c != null; })")]
    public void CompletionMode_CompleteExpression_IsExpression(string text) =>
        Assert.Equal(PredicateMode.Expression, PredicateCompiler.CompletionMode(text));

    [Theory]
    [InlineData("return true;")]
    [InlineData("var x = 1; return x > 0;")]
    [InlineData("if (n == null) return false; return true;")]
    [InlineData("throw new Exception();")]
    public void CompletionMode_RealBody_IsBody(string text) =>
        Assert.Equal(PredicateMode.Body, PredicateCompiler.CompletionMode(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CompletionMode_Empty_IsExpression(string text) =>
        Assert.Equal(PredicateMode.Expression, PredicateCompiler.CompletionMode(text));

    [Theory]
    [InlineData("n != null")]
    [InlineData("return true;")]
    [InlineData("var x = 1; return x > 0;")]
    public void CompletionMode_AgreesWithDetectMode_OnceTextIsComplete(string text) =>
        Assert.Equal(PredicateCompiler.DetectMode(text), PredicateCompiler.CompletionMode(text));
}

public class PredicateTemplateBodyModeTests
{
    public static TheoryData<int> AllKinds => new TheoryData<int>
    {
        (int)TargetKind.SyntaxNode,
        (int)TargetKind.SyntaxToken,
        (int)TargetKind.Operation,
    };

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Build_Body_OffsetPointsExactlyAtTheBodyText(int kindValue)
    {
        const string body = "var x = 1;\r\nreturn x > 0;";
        var source = PredicateTemplate.Build((TargetKind)kindValue, PredicateMode.Body, body, out var offset);

        Assert.Equal(body, source.Substring(offset, body.Length));
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Build_Body_DoesNotEmitAReturnPrefix(int kindValue)
    {
        var source = PredicateTemplate.Build((TargetKind)kindValue, PredicateMode.Body, "return true;", out var offset);

        Assert.DoesNotContain("return return", source);
        Assert.Equal("return true;", source.Substring(offset, "return true;".Length));
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Build_Body_ProducesValidSyntax(int kindValue)
    {
        var token = TestContext.Current.CancellationToken;
        var source = PredicateTemplate.Build(
            (TargetKind)kindValue,
            PredicateMode.Body,
            "var x = 1;\r\nforeach (var i in new[] { 1, 2 }) { x += i; }\r\nreturn x > 0;",
            out _);

        var tree = CSharpSyntaxTree.ParseText(source, PredicateTemplate.ParseOptions, cancellationToken: token);

        Assert.DoesNotContain(tree.GetDiagnostics(token), d => d.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_Body_EmptyFallsBackToReturnTrue(string body)
    {
        var source = PredicateTemplate.Build(TargetKind.SyntaxNode, PredicateMode.Body, body, out var offset);

        Assert.Equal("return true;", source.Substring(offset, "return true;".Length));
    }

    [Fact]
    public void Build_DefaultOverload_IsStillExpressionMode()
    {
        var viaDefault = PredicateTemplate.Build(TargetKind.SyntaxNode, "n != null", out var a);
        var viaExplicit = PredicateTemplate.Build(TargetKind.SyntaxNode, PredicateMode.Expression, "n != null", out var b);

        Assert.Equal(viaDefault, viaExplicit);
        Assert.Equal(a, b);
    }
}

[Collection(PredicateCompilerCacheCollection.Name)]
public class PredicateBodyModeCompilationTests
{
    private static async Task<(SyntaxNode node, SemanticModel model, Document doc)> LocalDeclarationAsync()
    {
        var workspace = new AdhocWorkspace();
        var projectInfo = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Create(),
            "BodyModeTestProject",
            "BodyModeTestProject",
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
    public async Task Compile_BodyWithLocalsAndLoop_Runs()
    {
        var (node, model, doc) = await LocalDeclarationAsync();

        const string body = @"
var count = 0;
foreach (var child in n.ChildNodes()) { count++; }
return count > 0;";

        var match = (NodeMatch)PredicateCompiler.Compile(TargetKind.SyntaxNode, body);

        Assert.True(match(node, model, doc));
    }

    [Fact]
    public async Task Compile_BodyWithEarlyReturn_Runs()
    {
        var (node, model, doc) = await LocalDeclarationAsync();

        const string body = "if (n == null) return false;\r\nvar t = n.ToString();\r\nreturn t.Length > 0;";

        var match = (NodeMatch)PredicateCompiler.Compile(TargetKind.SyntaxNode, body);

        Assert.True(match(node, model, doc));
    }

    [Fact]
    public async Task Compile_BodyReturningFalse_Runs()
    {
        var (node, model, doc) = await LocalDeclarationAsync();

        var match = (NodeMatch)PredicateCompiler.Compile(TargetKind.SyntaxNode, "var x = 1;\r\nreturn x < 0;");

        Assert.False(match(node, model, doc));
    }

    [Fact]
    public async Task Compile_ExpressionWithLambdaBlockBody_StillCompilesAsAnExpression()
    {
        var (node, model, doc) = await LocalDeclarationAsync();

        // Would be misrouted to body mode by a "contains a return token" check, and then fail to
        // compile because an expression is not a statement.
        var match = (NodeMatch)PredicateCompiler.Compile(TargetKind.SyntaxNode, "n.ChildNodes().Any(c => { return c != null; })");

        Assert.True(match(node, model, doc));
    }

    [Fact]
    public void Compile_SameTextDifferentModes_AreCachedSeparately()
    {
        // "return true;" is a body; forcing it through expression mode must not collide with it,
        // and must fail on its own terms rather than returning the body's delegate.
        var asBody = PredicateCompiler.Compile(TargetKind.SyntaxNode, PredicateMode.Body, "return true;");

        Assert.Throws<PredicateCompilationException>(
            () => PredicateCompiler.Compile(TargetKind.SyntaxNode, PredicateMode.Expression, "return true;"));

        Assert.NotNull(asBody);
    }

    [Fact]
    public void Compile_BodyModeIsAvailableForEveryTarget()
    {
        Assert.NotNull(PredicateCompiler.Compile(TargetKind.SyntaxNode, "return n != null;"));
        Assert.NotNull(PredicateCompiler.Compile(TargetKind.SyntaxToken, "return t.Text.Length > 0;"));
        Assert.NotNull(PredicateCompiler.Compile(TargetKind.Operation, "return op != null;"));
    }

    [Fact]
    public void Compile_BrokenBody_ReportsTheLineTheUserTyped()
    {
        var ex = Assert.Throws<PredicateCompilationException>(
            () => PredicateCompiler.Compile(TargetKind.SyntaxNode, "var x = 1;\r\nreturn nope;"));

        Assert.Contains("line 2", ex.Message);
    }

    [Fact]
    public void Compile_BrokenExpression_StillReportsABareColumn()
    {
        var ex = Assert.Throws<PredicateCompilationException>(
            () => PredicateCompiler.Compile(TargetKind.SyntaxNode, "nope"));

        Assert.Contains("col ", ex.Message);
        Assert.DoesNotContain("line ", ex.Message);
    }

    [Fact]
    public void Compile_ReformattedBody_SharesOneCacheEntry()
    {
        // Same tokens, different layout: one emitted assembly, not two. On net472 the second one
        // would never be reclaimed.
        var multiLine = PredicateCompiler.Compile(
            TargetKind.SyntaxNode,
            "var node = n as MemberAccessExpressionSyntax;\r\nreturn node is not null;");
        var singleLine = PredicateCompiler.Compile(
            TargetKind.SyntaxNode,
            "var node = n as MemberAccessExpressionSyntax; return node is not null;");

        Assert.Same(multiLine, singleLine);
    }

    [Fact]
    public void Compile_BrokenBody_ReportsTheColumnAsTyped()
    {
        // The normalized key for this collapses to "return nope ;" (col 8). Reporting col 11 is
        // what proves the diagnostic was mapped against the text the user actually typed.
        var ex = Assert.Throws<PredicateCompilationException>(
            () => PredicateCompiler.Compile(TargetKind.SyntaxNode, "var x   =   1;\r\nreturn    nope;"));

        Assert.Contains("line 2, col 11", ex.Message);
    }
}
