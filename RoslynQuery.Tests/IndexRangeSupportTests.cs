using System;
using System.Linq;
using System.Threading.Tasks;

using RoslynQuery.Query;

using Xunit;

namespace RoslynQuery.Tests;

/// <summary>
/// net472 has no <c>System.Index</c>/<c>System.Range</c>, and the copies inside System.Memory are internal to
/// it, so a predicate using <c>^x</c> or <c>a..b</c> failed with CS0518 until the templates started carrying
/// their own. Compiling for real is the only way to know: the operators are lowered against whatever types of
/// those names the compiler can see, so a shape that is subtly wrong fails here and nowhere else.
/// </summary>
[Collection(PredicateCompilerCacheCollection.Name)]
public class IndexRangeSupportTests
{
    private static int Token() => Guid.NewGuid().GetHashCode() & int.MaxValue;

    [Theory]
    [InlineData("n is MethodDeclarationSyntax m && m.ParameterList.Parameters[^1].Identifier.Text == \"{0}\"")]
    [InlineData("n.ToString()[^1] == ';' && n.ToString() != \"{0}\"")]
    [InlineData("n.ToString().ToCharArray()[^1] == ';' && n.ToString() != \"{0}\"")]
    [InlineData("n.ToString()[1..3] == \"{0}\"")]
    [InlineData("n.DescendantNodes().ToImmutableArray()[1..2].Length == 1 && n.ToString() != \"{0}\"")]
    [InlineData("var probe = ^1; return probe.Value == 1 && n.ToString() != \"{0}\";")]
    [InlineData("var probe = 1..3; return probe.End.Value == 3 && n.ToString() != \"{0}\";")]
    [InlineData("System.Index probe = 2; return probe.GetOffset(5) == 2 && n.ToString() != \"{0}\";")]
    [InlineData("System.Range probe = System.Range.All; return probe.Start.Value == 0 && n.ToString() != \"{0}\";")]
    public void IndexAndRangeOperators_Compile(string template) =>
        Assert.NotNull(PredicateCompiler.Compile(TargetKind.SyntaxNode, string.Format(template, Token())));

    [Fact]
    public void AReplacementExpression_GetsTheSameSupport() =>
        Assert.NotNull(Replace.ReplaceCompiler.Compile(
            TargetKind.SyntaxNode, $"n.ToString()[^1] == ';' && n.ToString() != \"{Token()}\" ? n : null"));

    /// <summary>
    /// Slicing an array needs <c>RuntimeHelpers.GetSubArray</c>, which only mscorlib can declare. Shadowing
    /// that type to add it would break every other use of it, so this stays unsupported on purpose.
    /// </summary>
    [Fact]
    public void SlicingAnArray_IsStillUnsupported()
    {
        var text = $"n.ToString().ToCharArray()[1..3].Length == 2 && n.ToString() != \"{Token()}\"";

        var failure = Assert.Throws<PredicateCompilationException>(
            () => PredicateCompiler.Compile(TargetKind.SyntaxNode, text));

        Assert.Contains("GetSubArray", failure.Message);
    }

    [Fact]
    public void TheSupportSource_IsAppendedAfterTheUsersText()
    {
        var source = PredicateTemplate.Build(TargetKind.SyntaxNode, "n != null", out var offset);

        Assert.EndsWith(ExpressionSupport.IndexRangeSupport, source);
        Assert.True(source.IndexOf("struct Index", StringComparison.Ordinal) > offset);
    }

    [Fact]
    public void TheSupportTypes_StayOutOfTheUsersWay()
    {
        // Internal, so nothing a predicate returns can expose them, and declared in System so the compiler
        // finds them where it looks for the well-known types.
        Assert.Contains("namespace System", ExpressionSupport.IndexRangeSupport);
        Assert.Contains("internal readonly struct Index", ExpressionSupport.IndexRangeSupport);
        Assert.Contains("internal readonly struct Range", ExpressionSupport.IndexRangeSupport);
        Assert.DoesNotContain("public readonly struct", ExpressionSupport.IndexRangeSupport);
    }

    [Theory]
    [InlineData(0, 5, 0)]
    [InlineData(2, 5, 2)]
    public async Task GetOffset_FromStart_IsTheValueItself(int value, int length, int expected) =>
        Assert.Equal(expected, await OffsetAsync(value, fromEnd: false, length));

    [Theory]
    [InlineData(1, 5, 4)]
    [InlineData(5, 5, 0)]
    public async Task GetOffset_FromEnd_CountsBackFromLength(int value, int length, int expected) =>
        Assert.Equal(expected, await OffsetAsync(value, fromEnd: true, length));

    /// <summary>Runs the polyfill's own arithmetic, since an off-by-one there would silently pick the wrong element.</summary>
    private static async Task<int> OffsetAsync(int value, bool fromEnd, int length)
    {
        var text = $"System.Index probe = new System.Index({value}, {(fromEnd ? "true" : "false")});"
            + $" return probe.GetOffset({length});";

        var predicate = PredicateCompiler.Compile(TargetKind.SyntaxNode, text);

        return (int)await (ValueTask<object>)predicate.DynamicInvoke(null, null, null);
    }
}
