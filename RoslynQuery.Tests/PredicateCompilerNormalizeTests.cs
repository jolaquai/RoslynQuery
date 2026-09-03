using System;
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

public class PredicateCompilerNormalizeTests
{
    private static string Normalize(string expression) => ExpressionSupport.Normalize(expression);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void NullOrWhitespace_NormalizesToEmpty(string expression) =>
        Assert.Equal(string.Empty, Normalize(expression));

    [Fact]
    public void AlreadyMinimal_IsUnchanged() =>
        Assert.Equal("n!=null", Normalize("n!=null"));

    [Fact]
    public void RedundantWhitespace_IsCollapsed()
    {
        Assert.Equal("n!=null", Normalize("n != null"));
        Assert.Equal("n!=null", Normalize("n  !=   null"));
        Assert.Equal("n!=null", Normalize("n\n!=\tnull"));
    }

    [Fact]
    public void SpaceAroundDotAccess_IsRemoved() =>
        Assert.Equal("n.Parent.Parent", Normalize("n . Parent . Parent"));

    [Fact]
    public void SpaceAroundStringLiteralConcat_IsRemoved() =>
        Assert.Equal("\"a\"+\"b\"", Normalize("\"a\" + \"b\""));

    [Fact]
    public void IdentifierIdentifierBoundary_KeepsRequiredSpace()
    {
        // Dropping the space here would re-lex "n" + "is" as the single identifier "nis".
        Assert.Equal("n is object", Normalize("n is object"));
        Assert.Equal("n is object", Normalize("n   is   object"));
    }

    [Fact]
    public void OperatorOperatorBoundary_KeepsRequiredSpace()
    {
        // Dropping the space would re-lex "-" + "-" as the decrement operator "--", changing
        // "1 - -1" (1 minus negative 1) into a syntactically different token stream.
        Assert.Equal("1- -1", Normalize("1 - -1"));
        Assert.Equal("1- -1", Normalize("1-  -1"));
    }

    [Fact]
    public void OperatorIdentifierBoundary_DropsUnneededSpace()
    {
        // "-" then "1": operator char followed by a digit never merges into one token, so no
        // space is required even though both immediately follow another operator-operator pair.
        Assert.Equal("a= -1", Normalize("a = - 1"));
    }

    [Fact]
    public void KeywordOperatorBoundary_DropsUnneededSpace() =>
        Assert.Equal("true&&false", Normalize("true && false"));

    [Fact]
    public void NullCoalescingOperator_DropsUnneededSpace() =>
        Assert.Equal("n??true", Normalize("n ?? true"));

    [Fact]
    public void BlockComments_AreDropped() =>
        Assert.Equal("n!=null", Normalize("n /* not null check */ != null"));

    [Fact]
    public void LineComments_AreDropped() =>
        Assert.Equal("n!=null", Normalize("n != null // trailing note"));

    [Fact]
    public void LeadingAndTrailingComments_AreDropped() =>
        Assert.Equal("n!=null", Normalize("// leading\nn != null /* trailing */"));

    [Theory]
    [InlineData("n != null")]
    [InlineData("n is object && n.Parent is not null")]
    [InlineData("1 - -1")]
    [InlineData("n?.Parent ?? n")]
    [InlineData("(n as object)?.ToString() == \"x\"")]
    public void Normalize_IsIdempotent(string expression)
    {
        var once = Normalize(expression);
        var twice = Normalize(once);

        Assert.Equal(once, twice);
    }

    [Theory]
    [InlineData("n != null", "n !=   null", "/* c */n!=null")]
    [InlineData("n is object", "n   is    object", "n is /* x */ object")]
    public void DifferentlyFormattedEquivalentExpressions_NormalizeToSameKey(string a, string b, string c)
    {
        var normalizedA = Normalize(a);
        var normalizedB = Normalize(b);
        var normalizedC = Normalize(c);

        Assert.Equal(normalizedA, normalizedB);
        Assert.Equal(normalizedA, normalizedC);
    }
}

// End-to-end coverage through PredicateCompiler.Compile: the normalizer's whole purpose is to fold
// differently-formatted-but-equivalent expressions to the same cache key, and to never change the
// meaning of the expression it normalizes. These exercise that contract via the real cache and a
// real compiled+invoked delegate rather than by re-deriving the normalizer's own logic.
[Collection(PredicateCompilerCacheCollection.Name)]
public class PredicateCompilerCachingTests
{
    [Fact]
    public void WhitespaceOnlyDifference_SharesCachedDelegate()
    {
        UniqueBoolExpression(out var a, out var b);

        var first = PredicateCompiler.Compile(TargetKind.SyntaxNode, a);
        var countAfterFirst = PredicateCompiler.CachedExpressionCount;
        var second = PredicateCompiler.Compile(TargetKind.SyntaxNode, b);

        Assert.Same(first, second);
        Assert.Equal(countAfterFirst, PredicateCompiler.CachedExpressionCount);
    }

    [Fact]
    public void CommentOnlyDifference_SharesCachedDelegate()
    {
        var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
        var a = $"true /* {suffix} */";
        var b = $"/* {suffix} */ true";

        // Both normalize to "true", which is already cached by other tests/expressions in this
        // run - so instead compare against each other directly rather than assuming a fresh key.
        var first = PredicateCompiler.Compile(TargetKind.SyntaxNode, a);
        var second = PredicateCompiler.Compile(TargetKind.SyntaxNode, b);

        Assert.Same(first, second);
    }

    [Fact]
    public void DifferentExpressions_GetDistinctDelegates()
    {
        var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
        var trueDelegate = PredicateCompiler.Compile(TargetKind.SyntaxNode, $"true /* {suffix} */");
        var falseDelegate = PredicateCompiler.Compile(TargetKind.SyntaxNode, $"false /* {suffix} */");

        Assert.NotSame(trueDelegate, falseDelegate);
    }

    // Generates two expressions that are token-for-token different in whitespace only, but unique
    // to this test invocation (via an embedded numeric literal) so the cache entry can't already
    // have been populated by another test.
    private static void UniqueBoolExpression(out string spaced, out string tight)
    {
        var token = Guid.NewGuid().GetHashCode() & int.MaxValue;
        spaced = $"true || {token} == {token}";
        tight = $"true||{token}=={token}";
    }

    [Fact]
    public async Task NormalizationPreservesSemantics_CompiledDelegateBehavesCorrectly()
    {
        using var workspace = new AdhocWorkspace();
        var projectInfo = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Create(),
            "NormalizeSemanticsTestProject",
            "NormalizeSemanticsTestProject",
            LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });

        var project = workspace.AddProject(projectInfo);
        var document = workspace.AddDocument(project.Id, "Test.cs", SourceText.From("class C { void M() { object x = null; } }"));

        var token = TestContext.Current.CancellationToken;
        var semanticModel = await document.GetSemanticModelAsync(token);
        var root = await document.GetSyntaxRootAsync(token);
        var node = root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>().First();

        // Deliberately over-formatted with redundant whitespace and a comment, to force the
        // normalizer to rewrite it before compilation; if normalization ever mangled the
        // expression's meaning this would fail to compile or evaluate incorrectly.
        const string overFormatted = "n   is  /* local decl */  LocalDeclarationStatementSyntax   &&   n . Parent  !=  null";

        var del = (NodeMatch)PredicateCompiler.Compile(TargetKind.SyntaxNode, overFormatted);

        Assert.Equal(true, await del(node, semanticModel, document));
    }
}
