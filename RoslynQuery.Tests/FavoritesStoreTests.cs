using System;
using System.IO;
using System.Linq;

using RoslynQuery.Query;
using RoslynQuery.ToolWindow;

using Xunit;

namespace RoslynQuery.Tests;

/// <summary>
/// One class on purpose: <c>FavoritesStore</c> is a process-wide static, and xUnit only serializes
/// test methods within a single class. A second class touching it would race this one.
/// </summary>
public sealed class FavoritesStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "RoslynQueryTests", Guid.NewGuid().ToString("N"));

    public FavoritesStoreTests() => FavoritesStore.DirectoryOverride = _directory;

    public void Dispose()
    {
        FavoritesStore.DirectoryOverride = null;
        try
        { Directory.Delete(_directory, recursive: true); }
        catch (Exception) { }
    }

    /// <summary>Drops the in-memory list so the next read comes back off disk, as after a devenv restart.</summary>
    private void Reload() => FavoritesStore.DirectoryOverride = _directory;

    [Fact]
    public void All_WithNothingStarred_IsEmpty() => Assert.Empty(FavoritesStore.All);

    [Fact]
    public void Add_ThenContains_FindsTheEntry()
    {
        FavoritesStore.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "n != null");

        Assert.True(FavoritesStore.Contains(TargetKind.SyntaxNode, PredicateMode.Expression, "n != null"));
    }

    [Fact]
    public void Contains_DistinguishesKindAndMode()
    {
        FavoritesStore.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "n != null");

        Assert.False(FavoritesStore.Contains(TargetKind.SyntaxToken, PredicateMode.Expression, "n != null"));
        Assert.False(FavoritesStore.Contains(TargetKind.SyntaxNode, PredicateMode.Body, "n != null"));
    }

    [Fact]
    public void Add_OrdersMostRecentlyStarredFirst()
    {
        FavoritesStore.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "first");
        FavoritesStore.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "second");

        Assert.Equal(["second", "first"], FavoritesStore.All.Select(e => e.Text));
    }

    [Fact]
    public void Add_ExistingEntry_MovesItToFrontWithoutDuplicating()
    {
        FavoritesStore.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "a");
        FavoritesStore.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "b");
        FavoritesStore.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "a");

        Assert.Equal(["a", "b"], FavoritesStore.All.Select(e => e.Text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Add_EmptyText_IsIgnored(string text)
    {
        FavoritesStore.Add(TargetKind.SyntaxNode, PredicateMode.Expression, text);

        Assert.Empty(FavoritesStore.All);
    }

    [Fact]
    public void Remove_DropsOnlyTheMatchingEntry()
    {
        FavoritesStore.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "a");
        FavoritesStore.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "b");

        FavoritesStore.Remove(TargetKind.SyntaxNode, PredicateMode.Expression, "a");

        Assert.Equal(["b"], FavoritesStore.All.Select(e => e.Text));
    }

    [Fact]
    public void Remove_UnknownEntry_IsANoOp()
    {
        FavoritesStore.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "a");

        FavoritesStore.Remove(TargetKind.Operation, PredicateMode.Body, "nope");

        Assert.Equal(["a"], FavoritesStore.All.Select(e => e.Text));
    }

    [Fact]
    public void Entries_SurviveAReload()
    {
        FavoritesStore.Add(TargetKind.Operation, PredicateMode.Body, "return op != null;");
        FavoritesStore.Add(TargetKind.SyntaxToken, PredicateMode.Expression, "t.ValueText == \"x\"");

        Reload();

        Assert.Equal(
            [(TargetKind.SyntaxToken, PredicateMode.Expression, "t.ValueText == \"x\""),
             (TargetKind.Operation, PredicateMode.Body, "return op != null;")],
            FavoritesStore.All);
    }

    [Fact]
    public void Remove_SurvivesAReload()
    {
        FavoritesStore.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "a");
        FavoritesStore.Remove(TargetKind.SyntaxNode, PredicateMode.Expression, "a");

        Reload();

        Assert.Empty(FavoritesStore.All);
    }

    [Theory]
    // A verbatim or raw string literal carries its source text into the normalized cache key, so a
    // favorite genuinely can contain the separator and newlines the file format is built on.
    [InlineData("n.ToString() == @\"a\r\nb\"")]
    [InlineData("n.ToString() == \"a\tb\"")]
    [InlineData("n.ToString() == \"c:\\\\temp\"")]
    [InlineData("n.ToString() == \"trailing backslash \\\\\"")]
    [InlineData("n.ToString() == \"\\\\n not a newline\"")]
    public void Text_WithSeparatorsOrEscapes_RoundTripsThroughDisk(string text)
    {
        FavoritesStore.Add(TargetKind.SyntaxNode, PredicateMode.Expression, text);

        Reload();

        Assert.Equal([text], FavoritesStore.All.Select(e => e.Text));
    }

    [Fact]
    public void Add_IsUncapped_AndNeverDropsAnEarlierStar()
    {
        for (var i = 0; i < 1000; i++)
            FavoritesStore.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "q" + i);

        Reload();
        var all = FavoritesStore.All;

        Assert.Equal(1000, all.Count);
        Assert.Equal("q999", all[0].Text);
        Assert.Equal("q0", all[all.Count - 1].Text);
    }

    [Fact]
    public void Load_UnrecognizedHeader_YieldsNothing()
    {
        WriteRaw("roslynquery-favorites\t99\r\nSyntaxNode\tExpression\tn != null\r\n");

        Assert.Empty(FavoritesStore.All);
    }

    [Fact]
    public void Load_MissingFile_YieldsNothing()
    {
        Reload();

        Assert.Empty(FavoritesStore.All);
    }

    [Theory]
    [InlineData("SyntaxNode\tExpression")]
    [InlineData("SyntaxNode\tExpression\tn != null\textra")]
    [InlineData("NotAKind\tExpression\tn != null")]
    [InlineData("SyntaxNode\tNotAMode\tn != null")]
    [InlineData("7\tExpression\tn != null")]
    [InlineData("SyntaxNode\tExpression\t   ")]
    public void Load_MalformedLine_IsSkippedButLeavesGoodLines(string malformed)
    {
        WriteRaw("roslynquery-favorites\t1\r\n" + malformed + "\r\nSyntaxNode\tExpression\tgood\r\n");

        Assert.Equal(["good"], FavoritesStore.All.Select(e => e.Text));
    }

    [Fact]
    public void Load_DuplicateLines_CollapseToOne()
    {
        WriteRaw("roslynquery-favorites\t1\r\nSyntaxNode\tExpression\tdupe\r\nSyntaxNode\tExpression\tdupe\r\n");

        Assert.Equal(["dupe"], FavoritesStore.All.Select(e => e.Text));
    }

    private void WriteRaw(string contents)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "favorites.tsv"), contents, System.Text.Encoding.UTF8);
        Reload();
    }
}
