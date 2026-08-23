using System;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RoslynQuery.Query;
using RoslynQuery.Replace;

using Xunit;

namespace RoslynQuery.Tests;

// TargetKind is internal; Theory parameters must be publicly accessible types (CS0051), so kinds
// are passed as int and cast to TargetKind inside each test body.
public class ReplaceTemplateTests
{
    public static TheoryData<int> SupportedKinds =>
    [
        (int)TargetKind.SyntaxNode,
        (int)TargetKind.SyntaxToken,
    ];

    [Theory]
    [InlineData((int)TargetKind.SyntaxNode, "async ValueTask<object> (SyntaxNode n, SemanticModel model, Document doc)")]
    [InlineData((int)TargetKind.SyntaxToken, "async ValueTask<object> (SyntaxToken t, SemanticModel model, Document doc)")]
    public void Signature_ReturnsExpected(int kind, string expected) =>
        Assert.Equal(expected, ReplaceTemplate.Signature((TargetKind)kind));

    [Fact]
    public void Signature_Operation_Throws() =>
        Assert.Throws<NotSupportedException>(() => ReplaceTemplate.Signature(TargetKind.Operation));

    [Fact]
    public void Build_Operation_Throws() =>
        Assert.Throws<NotSupportedException>(() => ReplaceTemplate.Build(TargetKind.Operation, PredicateMode.Expression, "n", out _));

    [Theory]
    [MemberData(nameof(SupportedKinds))]
    public void Build_ContainsClassAndMethodDeclaration(int kindValue)
    {
        var kind = (TargetKind)kindValue;
        var source = ReplaceTemplate.Build(kind, PredicateMode.Expression, "null", out _);

        Assert.Contains($"public static class {ReplaceTemplate.ClassName}", source);
        Assert.Contains(
            $"public static async ValueTask<object> {ReplaceTemplate.MethodName}({PredicateTemplate.ParameterType(kind)} {PredicateTemplate.ParameterName(kind)}, SemanticModel model, Document doc)",
            source);
    }

    [Theory]
    [MemberData(nameof(SupportedKinds))]
    public void Build_ExpressionMode_WrapsInObjectCast(int kindValue)
    {
        const string expression = "\"x\"";
        var source = ReplaceTemplate.Build((TargetKind)kindValue, PredicateMode.Expression, expression, out var offset);

        Assert.EndsWith("return (object)(", source.Substring(0, offset));
        Assert.Equal(expression, source.Substring(offset, expression.Length));
        // A newline separates the expression from its closing ");", the same layout PredicateTemplate
        // uses between an expression and its terminating ";" (see Build_ExpressionIsTerminatedWithSemicolon).
        Assert.StartsWith(");", source.Substring(offset + expression.Length).TrimStart());
    }

    [Theory]
    [MemberData(nameof(SupportedKinds))]
    public void Build_BodyMode_EmptyText_ReturnsNull(int kindValue)
    {
        var source = ReplaceTemplate.Build((TargetKind)kindValue, PredicateMode.Body, "", out var offset);

        Assert.Equal("return null;", source.Substring(offset, "return null;".Length));
    }

    [Theory]
    [MemberData(nameof(SupportedKinds))]
    public void Build_ExpressionMode_EmptyText_SubstitutesNull(int kindValue)
    {
        var source = ReplaceTemplate.Build((TargetKind)kindValue, PredicateMode.Expression, "", out var offset);

        Assert.Equal("null", source.Substring(offset, 4));
    }

    [Theory]
    [MemberData(nameof(SupportedKinds))]
    public void Build_BodyMode_ProducesSyntacticallyValidCompilationUnit(int kindValue)
    {
        var token = TestContext.Current.CancellationToken;
        var source = ReplaceTemplate.Build((TargetKind)kindValue, PredicateMode.Body, "return null;", out _);

        var tree = CSharpSyntaxTree.ParseText(source, PredicateTemplate.ParseOptions, cancellationToken: token);
        var diagnostics = tree.GetDiagnostics(token).Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();

        Assert.Empty(diagnostics);
    }

    [Theory]
    [MemberData(nameof(SupportedKinds))]
    public void Build_ExpressionMode_ProducesSyntacticallyValidCompilationUnit(int kindValue)
    {
        var token = TestContext.Current.CancellationToken;
        var source = ReplaceTemplate.Build((TargetKind)kindValue, PredicateMode.Expression, "\"replacement\"", out _);

        var tree = CSharpSyntaxTree.ParseText(source, PredicateTemplate.ParseOptions, cancellationToken: token);
        var diagnostics = tree.GetDiagnostics(token).Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();

        Assert.Empty(diagnostics);
    }
}
