using RoslynQuery.Query;
using RoslynQuery.ToolWindow;

using Xunit;

namespace RoslynQuery.Tests;

public class CachedPredicateItemTests
{
    [Fact]
    public void Preview_ShortText_IsUnchanged()
    {
        var item = new CachedPredicateItem(TargetKind.SyntaxNode, PredicateMode.Expression, "n!=null");

        Assert.Equal("n!=null", item.Preview);
    }

    [Fact]
    public void Preview_ExactlyAtLimit_IsUnchanged()
    {
        var text = new string('a', 300);
        var item = new CachedPredicateItem(TargetKind.SyntaxNode, PredicateMode.Expression, text);

        Assert.Equal(text, item.Preview);
    }

    [Fact]
    public void Preview_OverLimit_IsTruncatedWithEllipsis()
    {
        var text = new string('a', 301);
        var item = new CachedPredicateItem(TargetKind.SyntaxNode, PredicateMode.Expression, text);

        Assert.Equal(new string('a', 300) + "...", item.Preview);
    }

    // TargetKind/PredicateMode are internal, so [Theory] data is carried as int (InlineData cannot
    // reference an internal enum as a typed argument), same workaround PredicateTemplateBodyModeTests
    // uses.
    [Theory]
    [InlineData((int)TargetKind.SyntaxNode, (int)PredicateMode.Expression, "SyntaxNode . Expression")]
    [InlineData((int)TargetKind.SyntaxToken, (int)PredicateMode.Body, "SyntaxToken . Body")]
    [InlineData((int)TargetKind.Operation, (int)PredicateMode.Expression, "Operation . Expression")]
    public void Subtitle_CombinesKindAndMode(int kindValue, int modeValue, string expected)
    {
        var item = new CachedPredicateItem((TargetKind)kindValue, (PredicateMode)modeValue, "text");

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
