using System;
using System.CodeDom.Compiler;
using System.IO;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RoslynQuery.Query;

using Xunit;

namespace RoslynQuery.Tests;

// TargetKind is internal; Theory parameters must be publicly accessible types (CS0051), so kinds
// are passed as int and cast to TargetKind inside each test body.
public class PredicateTemplateParameterTests
{
    [Theory]
    [InlineData((int)TargetKind.SyntaxNode, "n")]
    [InlineData((int)TargetKind.SyntaxToken, "t")]
    [InlineData((int)TargetKind.Operation, "op")]
    public void ParameterName_ReturnsExpected(int kind, string expected) =>
        Assert.Equal(expected, PredicateTemplate.ParameterName((TargetKind)kind));

    [Theory]
    [InlineData((int)TargetKind.SyntaxNode, "SyntaxNode")]
    [InlineData((int)TargetKind.SyntaxToken, "SyntaxToken")]
    [InlineData((int)TargetKind.Operation, "IOperation")]
    public void ParameterType_ReturnsExpected(int kind, string expected) =>
        Assert.Equal(expected, PredicateTemplate.ParameterType((TargetKind)kind));

    [Fact]
    public void ParameterName_InvalidKind_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PredicateTemplate.ParameterName((TargetKind)(-1)));

    [Fact]
    public void ParameterType_InvalidKind_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PredicateTemplate.ParameterType((TargetKind)(-1)));

    [Theory]
    [InlineData((int)TargetKind.SyntaxNode, "async ValueTask<bool> (SyntaxNode n, SemanticModel model, Document doc)")]
    [InlineData((int)TargetKind.SyntaxToken, "async ValueTask<bool> (SyntaxToken t, SemanticModel model, Document doc)")]
    [InlineData((int)TargetKind.Operation, "async ValueTask<bool> (IOperation op, SemanticModel model, Document doc)")]
    public void Signature_ReturnsExpected(int kind, string expected) =>
        Assert.Equal(expected, PredicateTemplate.Signature((TargetKind)kind));
}

public class PredicateTemplateBuildTests
{
    public static TheoryData<int> AllKinds =>
    [
        (int)TargetKind.SyntaxNode,
        (int)TargetKind.SyntaxToken,
        (int)TargetKind.Operation,
    ];

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Build_ContainsClassAndMethodDeclaration(int kindValue)
    {
        var kind = (TargetKind)kindValue;
        var source = PredicateTemplate.Build(kind, "true", out _);

        Assert.Contains($"public static class {PredicateTemplate.ClassName}", source);
        Assert.Contains(
            $"public static async ValueTask<bool> {PredicateTemplate.MethodName}({PredicateTemplate.ParameterType(kind)} {PredicateTemplate.ParameterName(kind)}, SemanticModel model, Document doc)",
            source);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Build_ContainsAllExpectedUsings(int kindValue)
    {
        var source = PredicateTemplate.Build((TargetKind)kindValue, "true", out _);

        foreach (var ns in new[]
                 {
                     "System", "System.Collections.Generic", "System.Collections.Immutable", "System.Linq",
                     "System.Text", "System.Text.RegularExpressions", "System.Threading.Tasks", "Microsoft.CodeAnalysis",
                     "Microsoft.CodeAnalysis.CSharp", "Microsoft.CodeAnalysis.CSharp.Syntax",
                     "Microsoft.CodeAnalysis.Operations", "Microsoft.CodeAnalysis.Text",
                 })
        {
            Assert.Contains($"using {ns};", source);
        }
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Build_BracesAreBalancedAndProperlyNested(int kindValue)
    {
        var source = PredicateTemplate.Build((TargetKind)kindValue, "n != null", out _);

        Assert.Equal(source.Count(c => c == '{'), source.Count(c => c == '}'));
        // class body brace, method body brace: exactly two levels of nesting.
        Assert.Equal(2, source.Count(c => c == '{'));
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Build_ExpressionOffset_PointsExactlyAtExpressionText(int kindValue)
    {
        const string expression = "n.IsKind(SyntaxKind.IdentifierName)";
        var source = PredicateTemplate.Build((TargetKind)kindValue, expression, out var offset);

        Assert.Equal(expression, source.Substring(offset, expression.Length));
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Build_ReturnStatement_PrecedesExpressionOffset(int kindValue)
    {
        const string expression = "true";
        var source = PredicateTemplate.Build((TargetKind)kindValue, expression, out var offset);

        Assert.EndsWith("return ", source.Substring(0, offset));
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Build_ExpressionIsTerminatedWithSemicolon(int kindValue)
    {
        const string expression = "n != null";
        var source = PredicateTemplate.Build((TargetKind)kindValue, expression, out var offset);

        var afterExpression = source.Substring(offset + expression.Length).TrimStart();
        Assert.StartsWith(";", afterExpression);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void Build_NullOrWhitespaceExpression_SubstitutesTrue(string expression)
    {
        var source = PredicateTemplate.Build(TargetKind.SyntaxNode, expression, out var offset);

        Assert.Equal("true", source.Substring(offset, 4));
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Build_ProducesSyntacticallyValidCompilationUnit(int kindValue)
    {
        var token = TestContext.Current.CancellationToken;
        var source = PredicateTemplate.Build((TargetKind)kindValue, "n != null", out _);

        var tree = CSharpSyntaxTree.ParseText(source, PredicateTemplate.ParseOptions, cancellationToken: token);
        var diagnostics = tree.GetDiagnostics(token).Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();

        Assert.Empty(diagnostics);
        Assert.True(tree.GetCompilationUnitRoot(token).Members.Count > 0);
    }

    [Fact]
    public void Build_EmptyExpression_StillProducesValidSyntax()
    {
        var token = TestContext.Current.CancellationToken;
        var source = PredicateTemplate.Build(TargetKind.SyntaxNode, "", out _);

        var tree = CSharpSyntaxTree.ParseText(source, PredicateTemplate.ParseOptions, cancellationToken: token);
        Assert.DoesNotContain(tree.GetDiagnostics(token), d => d.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Build_DifferentExpressions_OnlyExpressionTextDiffers(int kindValue)
    {
        var kind = (TargetKind)kindValue;
        var sourceA = PredicateTemplate.Build(kind, "true", out var offsetA);
        var sourceB = PredicateTemplate.Build(kind, "false", out var offsetB);

        Assert.Equal(offsetA, offsetB);
        Assert.Equal(sourceA.Substring(0, offsetA), sourceB.Substring(0, offsetB));
    }

    [Fact]
    public void ParseOptions_UsePreviewLanguageVersionAndNoDocumentationMode()
    {
        Assert.Equal(LanguageVersion.Preview, PredicateTemplate.ParseOptions.LanguageVersion);
        Assert.Equal(Microsoft.CodeAnalysis.DocumentationMode.None, PredicateTemplate.ParseOptions.DocumentationMode);
    }
}

public class IndentedTextWriterScopeTests
{
    [Fact]
    public void Scope_WritesOpeningAndClosingBraces()
    {
        using var sw = new StringWriter { NewLine = "\n" };
        using (var itw = new IndentedTextWriter(sw, "    "))
        {
            itw.Write("header");
            using (itw.Scope())
            {
                itw.Write("body");
            }
        }

        var result = sw.ToString();
        Assert.StartsWith("header{\n", result);
        Assert.EndsWith("}\n", result);
        Assert.Contains("    body", result);
    }

    [Fact]
    public void Scope_RestoresIndentLevelAfterDispose()
    {
        using var sw = new StringWriter();
        using var itw = new IndentedTextWriter(sw, "    ");

        Assert.Equal(0, itw.Indent);
        using (itw.Scope())
        {
            Assert.Equal(1, itw.Indent);
        }
        Assert.Equal(0, itw.Indent);
    }

    [Fact]
    public void Scope_NestedScopes_AccumulateIndent()
    {
        using var sw = new StringWriter();
        using var itw = new IndentedTextWriter(sw, "    ");

        using (itw.Scope())
        {
            Assert.Equal(1, itw.Indent);
            using (itw.Scope())
            {
                Assert.Equal(2, itw.Indent);
            }
            Assert.Equal(1, itw.Indent);
        }
        Assert.Equal(0, itw.Indent);
    }

    [Fact]
    public void Indent_DoesNotWriteBraces()
    {
        using var sw = new StringWriter { NewLine = "\n" };
        using (var itw = new IndentedTextWriter(sw, "    "))
        {
            using (itw.Indent())
            {
                Assert.Equal(1, itw.Indent);
                itw.Write("value");
            }
            Assert.Equal(0, itw.Indent);
        }

        var result = sw.ToString();
        Assert.DoesNotContain("{", result);
        Assert.DoesNotContain("}", result);
        Assert.Equal("value", result);
    }
}
