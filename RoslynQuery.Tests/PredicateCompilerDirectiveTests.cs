using System;

using RoslynQuery.Query;

using Xunit;

namespace RoslynQuery.Tests;

// ParseTokens evaluates directives against ParseOptions, which defines no preprocessor symbols, so
// "#if DEBUG a #else b #endif" lexes down to just "b" - the discarded branch is gone before
// anything downstream could notice it was ever there. Compile therefore rejects directive-bearing
// input outright instead of silently compiling whichever branch happened to survive.
[Collection(PredicateCompilerCacheCollection.Name)]
public class PredicateCompilerDirectiveTests
{
    private static string Normalize(string expression) => ExpressionSupport.Normalize(expression);

    private const string ConditionalExpression = "#if DEBUG\r\ntrue\r\n#else\r\nfalse\r\n#endif";

    [Fact]
    public void Compile_ConditionalExpression_IsRejected()
    {
        var ex = Assert.Throws<PredicateCompilationException>(
            () => PredicateCompiler.Compile(TargetKind.SyntaxNode, ConditionalExpression));

        Assert.Contains("#if DEBUG", ex.Message);
    }

    [Theory]
    [InlineData("#pragma warning disable CS0168\r\ntrue")]
    [InlineData("#region x\r\ntrue\r\n#endregion")]
    [InlineData("    #if DEBUG\r\ntrue\r\n    #endif")]
    [InlineData("#nullable enable\r\ntrue")]
    [InlineData("#define FOO\r\ntrue")]
    public void Compile_AnyDirective_IsRejected(string expression) =>
        Assert.Throws<PredicateCompilationException>(() => PredicateCompiler.Compile(TargetKind.SyntaxNode, expression));

    // Detection is lexer-based, so the refusal has to be precise: a "#if" that is inside a string
    // or commented out is not a directive and must still compile.
    [Fact]
    public void Compile_HashInsideStringLiteral_IsNotADirective() =>
        Assert.NotNull(PredicateCompiler.Compile(TargetKind.SyntaxNode, "n.ToString() == \"#if DEBUG\""));

    [Fact]
    public void Compile_DirectiveInsideBlockComment_IsNotADirective() =>
        // '#' is the first non-whitespace character on its line here, but it is comment trivia.
        Assert.NotNull(PredicateCompiler.Compile(TargetKind.SyntaxNode, "/*\r\n#if DEBUG\r\n*/\r\ntrue"));

    [Fact]
    public void Compile_DirectiveInsideLineComment_IsNotADirective() =>
        Assert.NotNull(PredicateCompiler.Compile(TargetKind.SyntaxNode, "// #if DEBUG\r\ntrue"));

    [Fact]
    public void Compile_HashOnItsOwnLineInsideVerbatimString_IsNotADirective() =>
        Assert.NotNull(PredicateCompiler.Compile(TargetKind.SyntaxNode, "n.ToString() == @\"\r\n#if DEBUG\r\n\""));

    [Fact]
    public void Compile_DirectiveRejection_DoesNotPoisonTheCache()
    {
        var before = PredicateCompiler.CachedExpressionCount;

        Assert.Throws<PredicateCompilationException>(
            () => PredicateCompiler.Compile(TargetKind.SyntaxNode, "#if DEBUG\r\ntrue\r\n#endif"));

        Assert.Equal(before, PredicateCompiler.CachedExpressionCount);
    }

    // Defence in depth: Normalize is unreachable with directives now that Compile rejects them
    // first, but it must stay safe if it is ever called on its own.
    [Fact]
    public void Normalize_ConditionalExpression_KeepsBothBranches()
    {
        var normalized = Normalize(ConditionalExpression);

        Assert.Contains("#if DEBUG", normalized);
        Assert.Contains("true", normalized);
        Assert.Contains("#else", normalized);
        Assert.Contains("false", normalized);
        Assert.Contains("#endif", normalized);
    }

    [Fact]
    public void Normalize_DirectiveBearingInput_IsDeterministic() =>
        Assert.Equal(Normalize(ConditionalExpression), Normalize(ConditionalExpression));
}
