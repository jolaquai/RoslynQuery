using System;
using System.IO;
using System.Linq;

using RoslynQuery.Query;
using RoslynQuery.Favorites;

using Xunit;

namespace RoslynQuery.Tests;

[Collection(FavoritesCollection.Name)]
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
    public void All_WithNothingStarred_IsEmpty() => Assert.Empty(FavoritesStore.Queries.All);

    [Fact]
    public void Add_ThenContains_FindsTheEntry()
    {
        FavoritesStore.Queries.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "n != null");

        Assert.True(FavoritesStore.Queries.Contains(TargetKind.SyntaxNode, PredicateMode.Expression, "n != null"));
    }

    [Fact]
    public void Contains_DistinguishesKindAndMode()
    {
        FavoritesStore.Queries.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "n != null");

        Assert.False(FavoritesStore.Queries.Contains(TargetKind.SyntaxToken, PredicateMode.Expression, "n != null"));
        Assert.False(FavoritesStore.Queries.Contains(TargetKind.SyntaxNode, PredicateMode.Body, "n != null"));
    }

    [Fact]
    public void Add_OrdersMostRecentlyStarredFirst()
    {
        FavoritesStore.Queries.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "first");
        FavoritesStore.Queries.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "second");

        Assert.Equal(["second", "first"], FavoritesStore.Queries.All.Select(e => e.Text));
    }

    [Fact]
    public void Add_ExistingEntry_MovesItToFrontWithoutDuplicating()
    {
        FavoritesStore.Queries.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "a");
        FavoritesStore.Queries.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "b");
        FavoritesStore.Queries.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "a");

        Assert.Equal(["a", "b"], FavoritesStore.Queries.All.Select(e => e.Text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Add_EmptyText_IsIgnored(string text)
    {
        FavoritesStore.Queries.Add(TargetKind.SyntaxNode, PredicateMode.Expression, text);

        Assert.Empty(FavoritesStore.Queries.All);
    }

    [Fact]
    public void Remove_DropsOnlyTheMatchingEntry()
    {
        FavoritesStore.Queries.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "a");
        FavoritesStore.Queries.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "b");

        FavoritesStore.Queries.Remove(TargetKind.SyntaxNode, PredicateMode.Expression, "a");

        Assert.Equal(["b"], FavoritesStore.Queries.All.Select(e => e.Text));
    }

    [Fact]
    public void Remove_UnknownEntry_IsANoOp()
    {
        FavoritesStore.Queries.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "a");

        FavoritesStore.Queries.Remove(TargetKind.Operation, PredicateMode.Body, "nope");

        Assert.Equal(["a"], FavoritesStore.Queries.All.Select(e => e.Text));
    }

    [Fact]
    public void Entries_SurviveAReload()
    {
        FavoritesStore.Queries.Add(TargetKind.Operation, PredicateMode.Body, "return op != null;");
        FavoritesStore.Queries.Add(TargetKind.SyntaxToken, PredicateMode.Expression, "t.ValueText == \"x\"");

        Reload();

        Assert.Equal(
            [new FavoriteEntry(TargetKind.SyntaxToken, PredicateMode.Expression, "t.ValueText == \"x\"", null),
             new FavoriteEntry(TargetKind.Operation, PredicateMode.Body, "return op != null;", null)],
            FavoritesStore.Queries.All);
    }

    [Fact]
    public void AName_SurvivesAReload()
    {
        FavoritesStore.Queries.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "n != null", "Non-null nodes");

        Reload();

        Assert.Equal("Non-null nodes", FavoritesStore.Queries.NameOf(TargetKind.SyntaxNode, PredicateMode.Expression, "n != null"));
    }

    [Fact]
    public void AName_IsNotPartOfTheKey()
    {
        FavoritesStore.Queries.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "n != null", "first");
        FavoritesStore.Queries.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "n != null", "second");

        var all = FavoritesStore.Queries.All;

        Assert.Equal("second", Assert.Single(all).Name);
    }

    [Fact]
    public void Rename_RelabelsInPlaceWithoutReordering()
    {
        FavoritesStore.Queries.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "a");
        FavoritesStore.Queries.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "b");

        FavoritesStore.Queries.Rename(TargetKind.SyntaxNode, PredicateMode.Expression, "a", "Ay");
        Reload();

        Assert.Equal(["b", "a"], FavoritesStore.Queries.All.Select(e => e.Text));
        Assert.Equal([null, "Ay"], FavoritesStore.Queries.All.Select(e => e.Name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Rename_ToNothing_ClearsTheName(string name)
    {
        FavoritesStore.Queries.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "a", "Ay");

        FavoritesStore.Queries.Rename(TargetKind.SyntaxNode, PredicateMode.Expression, "a", name);
        Reload();

        Assert.Null(FavoritesStore.Queries.NameOf(TargetKind.SyntaxNode, PredicateMode.Expression, "a"));
    }

    [Fact]
    public void Rename_AnUnstarredEntry_IsANoOp()
    {
        FavoritesStore.Queries.Rename(TargetKind.SyntaxNode, PredicateMode.Expression, "a", "Ay");

        Assert.Empty(FavoritesStore.Queries.All);
    }

    [Fact]
    public void AName_IsTrimmed()
    {
        FavoritesStore.Queries.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "a", "  padded  ");

        Assert.Equal("padded", FavoritesStore.Queries.NameOf(TargetKind.SyntaxNode, PredicateMode.Expression, "a"));
    }

    [Theory]
    [InlineData("tab\there")]
    [InlineData("line\r\nbreak")]
    [InlineData("back\\slash")]
    public void AName_WithSeparatorsOrEscapes_RoundTripsThroughDisk(string name)
    {
        FavoritesStore.Queries.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "a", name);

        Reload();

        Assert.Equal(name, FavoritesStore.Queries.NameOf(TargetKind.SyntaxNode, PredicateMode.Expression, "a"));
    }

    [Fact]
    public void NameOf_AnUnstarredEntry_IsNull() =>
        Assert.Null(FavoritesStore.Queries.NameOf(TargetKind.SyntaxNode, PredicateMode.Expression, "nope"));

    [Fact]
    public void Remove_SurvivesAReload()
    {
        FavoritesStore.Queries.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "a");
        FavoritesStore.Queries.Remove(TargetKind.SyntaxNode, PredicateMode.Expression, "a");

        Reload();

        Assert.Empty(FavoritesStore.Queries.All);
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
        FavoritesStore.Queries.Add(TargetKind.SyntaxNode, PredicateMode.Expression, text);

        Reload();

        Assert.Equal([text], FavoritesStore.Queries.All.Select(e => e.Text));
    }

    [Fact]
    public void Add_IsUncapped_AndNeverDropsAnEarlierStar()
    {
        for (var i = 0; i < 1000; i++)
            FavoritesStore.Queries.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "q" + i);

        Reload();
        var all = FavoritesStore.Queries.All;

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

        Assert.Empty(FavoritesStore.Queries.All);
    }

    [Fact]
    public void Load_TheCurrentStamp_IsVersionOne()
    {
        FavoritesStore.Queries.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "n != null");

        var lines = File.ReadAllLines(Path.Combine(_directory, "favorites.tsv"));

        Assert.Equal("roslynquery-favorites\t1", lines[0]);
    }

    [Fact]
    public void Load_MissingFile_YieldsNothing()
    {
        Reload();

        Assert.Empty(FavoritesStore.Queries.All);
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

        Assert.Equal(["good"], FavoritesStore.Queries.All.Select(e => e.Text));
    }

    [Fact]
    public void Load_DuplicateLines_CollapseToOne()
    {
        WriteRaw(Header + "\r\nSyntaxNode\tExpression\tdupe\tfirst\r\nSyntaxNode\tExpression\tdupe\tsecond\r\n");

        var entry = Assert.Single(FavoritesStore.Queries.All);

        Assert.Equal("dupe", entry.Text);
        Assert.Equal("first", entry.Name);
    }

    [Fact]
    public void Load_AnEmptyNameColumn_MeansNoName()
    {
        WriteRaw(Header + "\r\nSyntaxNode\tExpression\tn != null\t\r\n");

        Assert.Null(Assert.Single(FavoritesStore.Queries.All).Name);
    }

    private const string FutureFile = "roslynquery-favorites\t99\r\nSyntaxNode\tExpression\tfrom the future\t\r\n";

    private string Favorites => Path.Combine(_directory, "favorites.tsv");

    [Fact]
    public void ANewerFile_IsMovedAsideRatherThanRead()
    {
        WriteRaw(FutureFile);

        Assert.Empty(FavoritesStore.Queries.All);
        Assert.False(File.Exists(Favorites));
        Assert.Equal(FutureFile, File.ReadAllText(Favorites + ".v99.bak"));
    }

    [Fact]
    public void ANewerFile_IsReportedOnce()
    {
        WriteRaw(FutureFile);

        _ = FavoritesStore.Queries.All;
        var warning = FavoritesStore.Queries.TakeWarning();

        Assert.NotNull(warning);
        Assert.Contains("version 99", warning);
        Assert.Contains("understands 1", warning);
        Assert.Contains(".v99.bak", warning);
        Assert.Null(FavoritesStore.Queries.TakeWarning());
    }

    [Fact]
    public void AfterANewerFileIsMovedAside_StarringStartsAFreshFile()
    {
        WriteRaw(FutureFile);
        _ = FavoritesStore.Queries.All;

        FavoritesStore.Queries.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "mine");
        Reload();

        Assert.Equal(["mine"], FavoritesStore.Queries.All.Select(e => e.Text));
        Assert.True(File.Exists(Favorites + ".v99.bak"));
    }

    [Fact]
    public void ASecondNewerFile_NeverClobbersTheFirstBackup()
    {
        WriteRaw(FutureFile);
        _ = FavoritesStore.Queries.All;

        WriteRaw("roslynquery-favorites\t99\r\nSyntaxNode\tExpression\tsecond\t\r\n");
        _ = FavoritesStore.Queries.All;

        Assert.Equal(FutureFile, File.ReadAllText(Favorites + ".v99.bak"));
        Assert.Contains("second", File.ReadAllText(Favorites + ".v99-2.bak"));
    }

    /// <summary>Backing the file up is what makes writing safe, so a backup that fails has to stop the write.</summary>
    [Fact]
    public void ANewerFileThatCannotBeMoved_StopsEveryWriteInstead()
    {
        WriteRaw(FutureFile);

        // Shares reading so the file still parses, but withholds delete, which is what File.Move needs.
        using (File.Open(Favorites, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            _ = FavoritesStore.Queries.All;

            var warning = FavoritesStore.Queries.TakeWarning();
            Assert.NotNull(warning);
            Assert.Contains("will not be saved", warning);

            FavoritesStore.Queries.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "mine");
        }

        Assert.Equal(FutureFile, File.ReadAllText(Favorites));
    }

    [Fact]
    public void AFileAtTheCurrentVersion_IsNeverMovedAside()
    {
        FavoritesStore.Queries.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "mine");
        Reload();

        _ = FavoritesStore.Queries.All;

        Assert.Null(FavoritesStore.Queries.TakeWarning());
        Assert.True(File.Exists(Favorites));
        Assert.Empty(Directory.GetFiles(_directory, "*.bak"));
    }

    private const string Header = "roslynquery-favorites\t1";

    private void WriteRaw(string contents)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "favorites.tsv"), contents, System.Text.Encoding.UTF8);
        Reload();
    }
}
