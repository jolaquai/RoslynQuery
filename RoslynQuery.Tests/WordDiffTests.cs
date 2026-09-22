using System.Linq;

using RoslynQuery.ToolWindow;

using Xunit;

namespace RoslynQuery.Tests;

public class WordDiffTests
{
    [Fact]
    public void Compute_IdenticalText_IsAllSame()
    {
        var diff = WordDiff.Compute("int x = 1;", "int x = 1;");

        Assert.All(diff, d => Assert.Equal(WordDiffKind.Same, d.Kind));
        Assert.Equal("int x = 1;", string.Concat(diff.Select(d => d.Text)));
    }

    [Fact]
    public void Compute_OneWordChanged_OnlyThatWordDiffers()
    {
        var diff = WordDiff.Compute("int x = 1;", "int x = 2;");

        Assert.Contains(diff, d => d.Kind == WordDiffKind.Removed && d.Text == "1;");
        Assert.Contains(diff, d => d.Kind == WordDiffKind.Added && d.Text == "2;");
        // "int", " ", "x", " ", "=", " " - every token but the trailing number/whitespace-collapsed literal.
        Assert.Equal(6, diff.Count(d => d.Kind == WordDiffKind.Same));
    }

    [Fact]
    public void Compute_ReconstructsBothSidesFromTheSameSequence()
    {
        var diff = WordDiff.Compute("var result = Compute(a, b);", "var result = Compute(a, b, c);");

        var before = string.Concat(diff.Where(d => d.Kind != WordDiffKind.Added).Select(d => d.Text));
        var after = string.Concat(diff.Where(d => d.Kind != WordDiffKind.Removed).Select(d => d.Text));

        Assert.Equal("var result = Compute(a, b);", before);
        Assert.Equal("var result = Compute(a, b, c);", after);
    }

    [Fact]
    public void Compute_EmptyAfter_IsAllRemoved()
    {
        var diff = WordDiff.Compute("int x = 1;", "");

        Assert.All(diff, d => Assert.Equal(WordDiffKind.Removed, d.Kind));
        Assert.Equal("int x = 1;", string.Concat(diff.Select(d => d.Text)));
    }

    [Fact]
    public void Compute_EmptyBefore_IsAllAdded()
    {
        var diff = WordDiff.Compute("", "int x = 1;");

        Assert.All(diff, d => Assert.Equal(WordDiffKind.Added, d.Kind));
        Assert.Equal("int x = 1;", string.Concat(diff.Select(d => d.Text)));
    }

    [Fact]
    public void Compute_CompletelyDifferentText_IsAllRemovedThenAdded()
    {
        // Single tokens, no shared whitespace run between them, so nothing can align as Same.
        var diff = WordDiff.Compute("foo", "bar");

        Assert.DoesNotContain(diff, d => d.Kind == WordDiffKind.Same);
        Assert.Contains(diff, d => d.Kind == WordDiffKind.Removed && d.Text == "foo");
        Assert.Contains(diff, d => d.Kind == WordDiffKind.Added && d.Text == "bar");
    }
}
