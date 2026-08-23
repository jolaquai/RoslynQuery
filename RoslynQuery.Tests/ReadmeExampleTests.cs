using RoslynQuery.Query;

using Xunit;

namespace RoslynQuery.Tests;

/// <summary>
/// Every predicate printed in README.md, compiled. A documented example that does not compile is
/// worse than no example, and the README is the one place nothing else exercises - the IOperation
/// one below shipped broken (<c>IConversionOperation.Conversion</c> is a <c>CommonConversion</c>,
/// which has no <c>IsBoxing</c>) until this was added.
/// </summary>
/// <remarks>Keep in sync with README.md; if an example changes there, change it here.</remarks>
[Collection(PredicateCompilerCacheCollection.Name)]
public class ReadmeExampleTests
{
    [Theory]
    [InlineData("n is MethodDeclarationSyntax m && m.ParameterList.Parameters.Count > 3")]
    [InlineData("var m = n as MethodDeclarationSyntax;\r\nif (m is null) return false;\r\nreturn m.Body?.Statements.Count > 20;")]
    [InlineData("(await doc.GetSyntaxRootAsync()).DescendantNodes().Count() > 500")]
    [InlineData("n.IsKind(SyntaxKind.IfStatement)")]
    [InlineData("n is InvocationExpressionSyntax i && i.ArgumentList.Arguments.Count > 4")]
    [InlineData("n is MethodDeclarationSyntax m && m.Modifiers.Any(SyntaxKind.AsyncKeyword)\r\n    && !m.Identifier.Text.EndsWith(\"Async\")")]
    [InlineData("n is IdentifierNameSyntax id && model.GetSymbolInfo(id).Symbol is IMethodSymbol { IsStatic: true }")]
    public void SyntaxNodeExamples_Compile(string text) =>
        Assert.NotNull(PredicateCompiler.Compile(TargetKind.SyntaxNode, text));

    [Theory]
    [InlineData("t.IsKind(SyntaxKind.StringLiteralToken) && t.ValueText.Length > 200")]
    public void SyntaxTokenExamples_Compile(string text) =>
        Assert.NotNull(PredicateCompiler.Compile(TargetKind.SyntaxToken, text));

    [Theory]
    [InlineData("op is IConversionOperation c && c.GetConversion().IsBoxing")]
    public void OperationExamples_Compile(string text) =>
        Assert.NotNull(PredicateCompiler.Compile(TargetKind.Operation, text));

    [Theory]
    // The README claims mode is detected from the text, so the expression examples must not be
    // taken for bodies and the body example must not be taken for an expression.
    [InlineData("n.IsKind(SyntaxKind.IfStatement)", (int)PredicateMode.Expression)]
    [InlineData("(await doc.GetSyntaxRootAsync()).DescendantNodes().Count() > 500", (int)PredicateMode.Expression)]
    [InlineData("var m = n as MethodDeclarationSyntax;\r\nif (m is null) return false;\r\nreturn m.Body?.Statements.Count > 20;", (int)PredicateMode.Body)]
    public void Examples_DetectTheDocumentedMode(string text, int expectedMode) =>
        Assert.Equal((PredicateMode)expectedMode, ExpressionSupport.DetectMode(text));
}
