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
            [new FavoritesStore.Entry(TargetKind.SyntaxToken, PredicateMode.Expression, "t.ValueText == \"x\"", null),
             new FavoritesStore.Entry(TargetKind.Operation, PredicateMode.Body, "return op != null;", null)],
            FavoritesStore.All);
    }

    [Fact]
    public void AName_SurvivesAReload()
    {
        FavoritesStore.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "n != null", "Non-null nodes");

        Reload();

        Assert.Equal("Non-null nodes", FavoritesStore.NameOf(TargetKind.SyntaxNode, PredicateMode.Expression, "n != null"));
    }

    [Fact]
    public void AName_IsNotPartOfTheKey()
    {
        FavoritesStore.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "n != null", "first");
        FavoritesStore.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "n != null", "second");

        var all = FavoritesStore.All;

        Assert.Equal("second", Assert.Single(all).Name);
    }

    [Fact]
    public void Rename_RelabelsInPlaceWithoutReordering()
    {
        FavoritesStore.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "a");
        FavoritesStore.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "b");

        FavoritesStore.Rename(TargetKind.SyntaxNode, PredicateMode.Expression, "a", "Ay");
        Reload();

        Assert.Equal(["b", "a"], FavoritesStore.All.Select(e => e.Text));
        Assert.Equal([null, "Ay"], FavoritesStore.All.Select(e => e.Name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Rename_ToNothing_ClearsTheName(string name)
    {
        FavoritesStore.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "a", "Ay");

        FavoritesStore.Rename(TargetKind.SyntaxNode, PredicateMode.Expression, "a", name);
        Reload();

        Assert.Null(FavoritesStore.NameOf(TargetKind.SyntaxNode, PredicateMode.Expression, "a"));
    }

    [Fact]
    public void Rename_AnUnstarredEntry_IsANoOp()
    {
        FavoritesStore.Rename(TargetKind.SyntaxNode, PredicateMode.Expression, "a", "Ay");

        Assert.Empty(FavoritesStore.All);
    }

    [Fact]
    public void AName_IsTrimmed()
    {
        FavoritesStore.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "a", "  padded  ");

        Assert.Equal("padded", FavoritesStore.NameOf(TargetKind.SyntaxNode, PredicateMode.Expression, "a"));
    }

    [Theory]
    [InlineData("tab\there")]
    [InlineData("line\r\nbreak")]
    [InlineData("back\\slash")]
    public void AName_WithSeparatorsOrEscapes_RoundTripsThroughDisk(string name)
    {
        FavoritesStore.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "a", name);

        Reload();

        Assert.Equal(name, FavoritesStore.NameOf(TargetKind.SyntaxNode, PredicateMode.Expression, "a"));
    }

    [Fact]
    public void NameOf_AnUnstarredEntry_IsNull() =>
        Assert.Null(FavoritesStore.NameOf(TargetKind.SyntaxNode, PredicateMode.Expression, "nope"));

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

    [Theory]
    // A version this extension does not know, a foreign format, and two stamps that are not a version at all.
    [InlineData("roslynquery-favorites\t99")]
    [InlineData("roslynquery-favorites\t0")]
    [InlineData("something-else\t1")]
    [InlineData("roslynquery-favorites")]
    [InlineData("roslynquery-favorites\tx")]
    [InlineData("roslynquery-favorites\t1\textra")]
    public void Load_AStampItCannotRead_YieldsNothing(string header)
    {
        WriteRaw(header + "\r\nSyntaxNode\tExpression\tn != null\t\r\n");

        Assert.Empty(FavoritesStore.All);
    }

    [Fact]
    public void Load_TheCurrentStamp_IsVersionOne()
    {
        FavoritesStore.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "n != null");

        var lines = File.ReadAllLines(Path.Combine(_directory, "favorites.tsv"));

        Assert.Equal("roslynquery-favorites\t1", lines[0]);
    }

    [Fact]
    public void Load_MissingFile_YieldsNothing()
    {
        Reload();

        Assert.Empty(FavoritesStore.All);
    }

    [Theory]
    [InlineData("SyntaxNode\tExpression")]
    [InlineData("SyntaxNode\tExpression\tn != null")]
    [InlineData("SyntaxNode\tExpression\tn != null\tname\textra")]
    [InlineData("NotAKind\tExpression\tn != null\t")]
    [InlineData("SyntaxNode\tNotAMode\tn != null\t")]
    [InlineData("7\tExpression\tn != null\t")]
    [InlineData("SyntaxNode\tExpression\t   \t")]
    public void Load_MalformedLine_IsSkippedButLeavesGoodLines(string malformed)
    {
        WriteRaw(Header + "\r\n" + malformed + "\r\nSyntaxNode\tExpression\tgood\t\r\n");

        Assert.Equal(["good"], FavoritesStore.All.Select(e => e.Text));
    }

    [Fact]
    public void Load_DuplicateLines_CollapseToOne()
    {
        WriteRaw(Header + "\r\nSyntaxNode\tExpression\tdupe\tfirst\r\nSyntaxNode\tExpression\tdupe\tsecond\r\n");

        var entry = Assert.Single(FavoritesStore.All);

        Assert.Equal("dupe", entry.Text);
        Assert.Equal("first", entry.Name);
    }

    [Fact]
    public void Load_AnEmptyNameColumn_MeansNoName()
    {
        WriteRaw(Header + "\r\nSyntaxNode\tExpression\tn != null\t\r\n");

        Assert.Null(Assert.Single(FavoritesStore.All).Name);
    }

    private const string Header = "roslynquery-favorites\t1";

    private void WriteRaw(string contents)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "favorites.tsv"), contents, System.Text.Encoding.UTF8);
        Reload();
    }
}
