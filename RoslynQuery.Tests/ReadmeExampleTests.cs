using System;
using System.Collections.Generic;
using System.Linq;

using RoslynQuery.Query;

using Xunit;

namespace RoslynQuery.Tests;

/// <summary>
/// Every predicate printed in README.md, read out of the README and compiled. A documented example that does
/// not compile is worse than no example, and the README is the one place nothing else exercises - the
/// IOperation one (<c>IConversionOperation.Conversion</c> is a <c>CommonConversion</c>, which has no
/// <c>IsBoxing</c>) shipped broken until this existed.
/// </summary>
[Collection(PredicateCompilerCacheCollection.Name)]
public class ReadmeExampleTests
{
    public static IEnumerable<object[]> SyntaxNodeExamples() => ReadmeExamples.For("SyntaxNode");

    public static IEnumerable<object[]> SyntaxTokenExamples() => ReadmeExamples.For("SyntaxToken");

    public static IEnumerable<object[]> OperationExamples() => ReadmeExamples.For("Operation");

    [Theory]
    [MemberData(nameof(SyntaxNodeExamples))]
    public void SyntaxNodeExamples_Compile(string text) =>
        Assert.NotNull(PredicateCompiler.Compile(TargetKind.SyntaxNode, text));

    [Theory]
    [MemberData(nameof(SyntaxTokenExamples))]
    public void SyntaxTokenExamples_Compile(string text) =>
        Assert.NotNull(PredicateCompiler.Compile(TargetKind.SyntaxToken, text));

    [Theory]
    [MemberData(nameof(OperationExamples))]
    public void OperationExamples_Compile(string text) =>
        Assert.NotNull(PredicateCompiler.Compile(TargetKind.Operation, text));

    /// <summary>
    /// Guards the extractor itself: if it silently stopped finding examples, every test above would pass by
    /// having nothing to run. The counts only have to be plausible, not exact.
    /// </summary>
    [Fact]
    public void TheExtractor_FindsExamplesForEveryTarget()
    {
        var all = ReadmeExamples.All();

        Assert.True(all.Count >= 20, "found only " + all.Count + " examples in the README");
        Assert.True(all.Count(e => e.Target == "SyntaxNode") >= 10);
        Assert.True(all.Count(e => e.Target == "SyntaxToken") >= 3);
        Assert.True(all.Count(e => e.Target == "Operation") >= 3);
        Assert.All(all, e => Assert.False(string.IsNullOrWhiteSpace(e.Text)));
    }

    /// <summary>The README claims the mode is detected from the text, so its own sections have to agree.</summary>
    [Theory]
    [MemberData(nameof(BodyExamples))]
    public void ExamplesUnderTheBodiesHeading_DetectAsBodies(string text) =>
        Assert.Equal(PredicateMode.Body, ExpressionSupport.DetectMode(text));

    [Theory]
    [MemberData(nameof(ExpressionExamples))]
    public void ExamplesUnderTheSyntaxOnlyHeading_DetectAsExpressions(string text) =>
        Assert.Equal(PredicateMode.Expression, ExpressionSupport.DetectMode(text));

    public static IEnumerable<object[]> BodyExamples() =>
        ReadmeExamples.All().Where(e => e.IsDocumentedAsBody).Select(e => new object[] { e.Text });

    public static IEnumerable<object[]> ExpressionExamples() =>
        ReadmeExamples.All()
            .Where(e => e.Section.IndexOf("syntax only", StringComparison.OrdinalIgnoreCase) >= 0)
            .Select(e => new object[] { e.Text });

    /// <summary>The "Need help?" link has to land on a heading that is actually there.</summary>
    [Fact]
    public void TheNeedHelpLink_PointsAtTheUsingItSection()
    {
        const string url = RoslynQuery.ToolWindow.QueryToolWindowControl.NeedHelpUrl;

        Assert.StartsWith("https://github.com/jolaquai/RoslynQuery/blob/main/README.md#", url);
        Assert.EndsWith("#using-it", url);
        Assert.Contains("\r\n## Using it\r\n", RepositoryFiles.Read("README.md"));
    }
}
