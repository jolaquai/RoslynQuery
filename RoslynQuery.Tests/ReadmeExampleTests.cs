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
    // Syntax only.
    [InlineData("n.IsKind(SyntaxKind.IfStatement)")]
    [InlineData("n is InvocationExpressionSyntax i && i.ArgumentList.Arguments.Count > 4")]
    [InlineData("n is MethodDeclarationSyntax m && m.Modifiers.Any(SyntaxKind.AsyncKeyword)\r\n    && !m.Identifier.Text.EndsWith(\"Async\")")]
    [InlineData("n is CatchClauseSyntax { Block.Statements.Count: 0 }")]
    [InlineData("n is IfStatementSyntax { Else: null, Statement: not BlockSyntax }")]
    [InlineData("n is ClassDeclarationSyntax c && c.Members.OfType<MethodDeclarationSyntax>().Count() > 20")]
    [InlineData("n.GetLeadingTrivia().Any(tr => tr.ToString().Contains(\"TODO\"))")]
    // With the semantic model.
    [InlineData("n is IdentifierNameSyntax id && model.GetSymbolInfo(id).Symbol is IMethodSymbol { IsStatic: true }")]
    [InlineData("n is IdentifierNameSyntax dep\r\n    && model.GetSymbolInfo(dep).Symbol is ISymbol s\r\n    && s.GetAttributes().Any(a => a.AttributeClass?.Name == \"ObsoleteAttribute\")")]
    [InlineData("n is ExpressionSyntax ex\r\n    && model.GetTypeInfo(ex) is { Type.IsValueType: true, ConvertedType.SpecialType: SpecialType.System_Object }")]
    [InlineData("n is PropertyDeclarationSyntax prop\r\n    && model.GetDeclaredSymbol(prop) is { DeclaredAccessibility: Accessibility.Public, SetMethod: not null }")]
    // Statement bodies.
    [InlineData("var m = n as MethodDeclarationSyntax;\r\nif (m is null) return false;\r\nreturn m.Body?.Statements.Count > 20;")]
    [InlineData("if (n is not InvocationExpressionSyntax call) return false;\r\nif (model.GetSymbolInfo(call).Symbol is not IMethodSymbol method) return false;\r\nreturn method.Name == \"ToString\" && method.Parameters.Length == 0;")]
    [InlineData("if (!n.IsKind(SyntaxKind.AwaitExpression)) return false;\r\nreturn n.FirstAncestorOrSelf<MethodDeclarationSyntax>();")]
    [InlineData("if (n.IsKind(SyntaxKind.IfStatement)) return n.FirstAncestorOrSelf<MethodDeclarationSyntax>();\r\nreturn false;")]
    // doc and await.
    [InlineData("(await doc.GetSyntaxRootAsync()).DescendantNodes().Count() > 500")]
    public void SyntaxNodeExamples_Compile(string text) =>
        Assert.NotNull(PredicateCompiler.Compile(TargetKind.SyntaxNode, text));

    [Theory]
    [InlineData("t.IsKind(SyntaxKind.StringLiteralToken) && t.ValueText.Length > 200")]
    [InlineData("t.IsKind(SyntaxKind.IdentifierToken) && t.ValueText.Length == 1")]
    [InlineData("t.LeadingTrivia.Any(tr => tr.IsKind(SyntaxKind.SingleLineCommentTrivia))")]
    public void SyntaxTokenExamples_Compile(string text) =>
        Assert.NotNull(PredicateCompiler.Compile(TargetKind.SyntaxToken, text));

    [Theory]
    [InlineData("op is IConversionOperation c && c.GetConversion().IsBoxing")]
    [InlineData("op is IInvocationOperation { TargetMethod.IsExtensionMethod: true }")]
    [InlineData("op is IArgumentOperation { ArgumentKind: ArgumentKind.DefaultValue }")]
    public void OperationExamples_Compile(string text) =>
        Assert.NotNull(PredicateCompiler.Compile(TargetKind.Operation, text));

    [Theory]
    // The README claims mode is detected from the text, so the expression examples must not be
    // taken for bodies and the body examples must not be taken for expressions.
    [InlineData("n.IsKind(SyntaxKind.IfStatement)", (int)PredicateMode.Expression)]
    [InlineData("(await doc.GetSyntaxRootAsync()).DescendantNodes().Count() > 500", (int)PredicateMode.Expression)]
    [InlineData("n is CatchClauseSyntax { Block.Statements.Count: 0 }", (int)PredicateMode.Expression)]
    [InlineData("n is ExpressionSyntax ex\r\n    && model.GetTypeInfo(ex) is { Type.IsValueType: true, ConvertedType.SpecialType: SpecialType.System_Object }", (int)PredicateMode.Expression)]
    [InlineData("var m = n as MethodDeclarationSyntax;\r\nif (m is null) return false;\r\nreturn m.Body?.Statements.Count > 20;", (int)PredicateMode.Body)]
    [InlineData("if (!n.IsKind(SyntaxKind.AwaitExpression)) return false;\r\nreturn n.FirstAncestorOrSelf<MethodDeclarationSyntax>();", (int)PredicateMode.Body)]
    public void Examples_DetectTheDocumentedMode(string text, int expectedMode) =>
        Assert.Equal((PredicateMode)expectedMode, ExpressionSupport.DetectMode(text));

    /// <summary>The "Get started" link has to land on a heading that is actually there.</summary>
    [Fact]
    public void TheGetStartedLink_PointsAtTheUsingItSection()
    {
        const string url = RoslynQuery.ToolWindow.QueryToolWindowControl.GetStartedUrl;

        Assert.StartsWith("https://github.com/jolaquai/RoslynQuery/blob/main/README.md#", url);
        Assert.EndsWith("#using-it", url);
        Assert.Contains("\r\n## Using it\r\n", RepositoryFiles.Read("README.md"));
    }
}
