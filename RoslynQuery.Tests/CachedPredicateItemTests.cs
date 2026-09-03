using System.Linq;

using RoslynQuery.Mcp.Contracts;
using RoslynQuery.Query;
using RoslynQuery.ToolWindow;

using Xunit;

namespace RoslynQuery.Tests;

public class CachedPredicateItemTests
{
    [Fact]
    public void Display_ShortText_IsUnchanged()
    {
        var item = new CachedPredicateItem(TargetKind.SyntaxNode, PredicateMode.Expression, "n!=null");

        Assert.Equal("n != null", item.Display);
    }

    // A long but genuinely valid expression, so these exercise the truncation boundary rather than
    // the formatter's fallback path.
    private static string LongExpression() => string.Join(" || ", Enumerable.Repeat("n != null", 40));

    [Fact]
    public void Display_OverLimit_IsTruncatedWithEllipsis()
    {
        var item = new CachedPredicateItem(TargetKind.SyntaxNode, PredicateMode.Expression, LongExpression());

        Assert.EndsWith("...", item.Display);
        Assert.Equal(303, item.Display.Length);
    }

    [Fact]
    public void Pretty_IsNeverTruncated()
    {
        var item = new CachedPredicateItem(TargetKind.SyntaxNode, PredicateMode.Expression, LongExpression());

        // Restoring happens from Pretty; a truncated restore would silently run a fragment.
        Assert.True(item.Pretty.Length > 300);
        Assert.DoesNotContain("...", item.Pretty);
    }

    [Theory]
    // The body normalizer emits a separator between every pair of tokens, which is what these
    // entries look like coming out of the cache.
    [InlineData("n . Parent != null", "n.Parent != null")]
    [InlineData("n.IsKind ( SyntaxKind.IfStatement )", "n.IsKind(SyntaxKind.IfStatement)")]
    [InlineData("n!=null", "n != null")]
    public void Pretty_Expression_IsReformatted(string stored, string expected)
    {
        var item = new CachedPredicateItem(TargetKind.SyntaxNode, PredicateMode.Expression, stored);

        Assert.Equal(expected, item.Pretty);
    }

    [Fact]
    public void Pretty_Body_IsReformattedOnePerLineWithoutWrappingBraces()
    {
        var item = new CachedPredicateItem(
            TargetKind.SyntaxNode,
            PredicateMode.Body,
            "await Task . Yield ( ) ; return true ;");

        var lines = item.Pretty.Split(['\r', '\n'], System.StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(["await Task.Yield();", "return true;"], lines);
        Assert.DoesNotContain("{", item.Pretty);
        Assert.DoesNotContain("}", item.Pretty);
    }

    [Fact]
    public void Pretty_UnparseableText_FallsBackToTheStoredText()
    {
        const string junk = "this is ((( not c#";
        var item = new CachedPredicateItem(TargetKind.SyntaxNode, PredicateMode.Expression, junk);

        Assert.Equal(junk, item.Pretty);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Pretty_Empty_IsEmpty(string text)
    {
        var item = new CachedPredicateItem(TargetKind.SyntaxNode, PredicateMode.Expression, text);

        Assert.Equal(string.Empty, item.Pretty);
    }

    // TargetKind/PredicateMode are internal, so [Theory] data is carried as int (InlineData cannot
    // reference an internal enum as a typed argument), same workaround PredicateTemplateBodyModeTests
    // uses.
    [Theory]
    [InlineData((int)TargetKind.SyntaxNode, (int)PredicateMode.Expression, "SyntaxNode")]
    [InlineData((int)TargetKind.SyntaxToken, (int)PredicateMode.Body, "SyntaxToken (body)")]
    [InlineData((int)TargetKind.Operation, (int)PredicateMode.Expression, "Operation")]
    public void Subtitle_NamesTheKindAndFlagsBodyModeOnly(int kindValue, int modeValue, string expected)
    {
        var item = new CachedPredicateItem((TargetKind)kindValue, (PredicateMode)modeValue, "n != null");

        Assert.Equal(expected, item.Subtitle);
    }

    [Fact]
    public void Constructor_ExposesKindModeAndTextUnchanged()
    {
        var item = new CachedPredicateItem(TargetKind.Operation, PredicateMode.Body, "return op != null;");

        Assert.Equal(TargetKind.Operation, item.Kind);
        Assert.Equal(PredicateMode.Body, item.Mode);
        Assert.Equal("return op != null;", item.Text);
    }
}
