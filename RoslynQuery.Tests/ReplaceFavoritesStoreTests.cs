using System;
using System.IO;
using System.Linq;

using RoslynQuery.Favorites;
using RoslynQuery.Query;

using Xunit;

namespace RoslynQuery.Tests;

[Collection(FavoritesCollection.Name)]
public sealed class ReplaceFavoritesStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "RoslynQueryTests", Guid.NewGuid().ToString("N"));

    public ReplaceFavoritesStoreTests() => FavoritesStore.DirectoryOverride = _directory;

    public void Dispose()
    {
        FavoritesStore.DirectoryOverride = null;
        try
        { Directory.Delete(_directory, recursive: true); }
        catch (Exception) { }
    }

    private void Reload() => FavoritesStore.DirectoryOverride = _directory;

    private string ReplaceFile => Path.Combine(_directory, "replace-favorites.tsv");

    private string QueryFile => Path.Combine(_directory, "favorites.tsv");

    [Fact]
    public void Entries_SurviveAReload()
    {
        FavoritesStore.Replacements.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "\"x\"");
        FavoritesStore.Replacements.Add(TargetKind.SyntaxToken, PredicateMode.Body, "return t.Text;", "Same text");

        Reload();

        Assert.Equal(
            [new FavoriteEntry(TargetKind.SyntaxToken, PredicateMode.Body, "return t.Text;", "Same text"),
             new FavoriteEntry(TargetKind.SyntaxNode, PredicateMode.Expression, "\"x\"", null)],
            FavoritesStore.Replacements.All);
    }

    [Fact]
    public void Add_OrdersMostRecentlyStarredFirst_WithoutDuplicating()
    {
        FavoritesStore.Replacements.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "a");
        FavoritesStore.Replacements.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "b");
        FavoritesStore.Replacements.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "a");

        Assert.Equal(["a", "b"], FavoritesStore.Replacements.All.Select(e => e.Text));
    }

    [Fact]
    public void Rename_RelabelsInPlaceAndSurvivesAReload()
    {
        FavoritesStore.Replacements.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "a");
        FavoritesStore.Replacements.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "b");

        FavoritesStore.Replacements.Rename(TargetKind.SyntaxNode, PredicateMode.Expression, "a", "Ay");
        Reload();

        Assert.Equal(["b", "a"], FavoritesStore.Replacements.All.Select(e => e.Text));
        Assert.Equal("Ay", FavoritesStore.Replacements.NameOf(TargetKind.SyntaxNode, PredicateMode.Expression, "a"));
    }

    [Fact]
    public void Remove_SurvivesAReload()
    {
        FavoritesStore.Replacements.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "a");
        FavoritesStore.Replacements.Remove(TargetKind.SyntaxNode, PredicateMode.Expression, "a");

        Reload();

        Assert.Empty(FavoritesStore.Replacements.All);
    }

    [Fact]
    public void TheStamp_IsItsOwnFormatAtVersionOne()
    {
        FavoritesStore.Replacements.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "\"x\"");

        Assert.Equal("roslynquery-replace-favorites\t1", File.ReadAllLines(ReplaceFile)[0]);
    }

    [Fact]
    public void Starring_LeavesTheOtherStoreAlone()
    {
        FavoritesStore.Queries.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "n != null");
        var queryBytes = File.ReadAllBytes(QueryFile);

        FavoritesStore.Replacements.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "\"x\"");
        Reload();

        Assert.Equal(queryBytes, File.ReadAllBytes(QueryFile));
        Assert.Equal(["n != null"], FavoritesStore.Queries.All.Select(e => e.Text));
        Assert.Equal(["\"x\""], FavoritesStore.Replacements.All.Select(e => e.Text));
    }

    [Fact]
    public void StarringAQuery_NeverCreatesTheReplaceFile()
    {
        FavoritesStore.Queries.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "n != null");

        Assert.False(File.Exists(ReplaceFile));
        Assert.Empty(FavoritesStore.Replacements.All);
    }

    [Fact]
    public void AQueryFavoritesFile_IsNotReadAsReplacements()
    {
        WriteRaw("roslynquery-favorites\t1\r\nSyntaxNode\tExpression\tn != null\t\r\n");

        Assert.Empty(FavoritesStore.Replacements.All);
    }

    private const string FutureFile = "roslynquery-replace-favorites\t99\r\nSyntaxNode\tExpression\tfrom the future\t\r\n";

    [Fact]
    public void ANewerFile_IsMovedAsideAndReportedOnce()
    {
        WriteRaw(FutureFile);

        Assert.Empty(FavoritesStore.Replacements.All);
        Assert.False(File.Exists(ReplaceFile));
        Assert.Equal(FutureFile, File.ReadAllText(ReplaceFile + ".v99.bak"));

        var warning = FavoritesStore.Replacements.TakeWarning();
        Assert.Contains("version 99", warning);
        Assert.Contains("replace-favorites.tsv.v99.bak", warning);
        Assert.Null(FavoritesStore.Replacements.TakeWarning());
        Assert.Null(FavoritesStore.Queries.TakeWarning());
    }

    [Fact]
    public void ANewerFileThatCannotBeMoved_StopsEveryWriteInstead()
    {
        WriteRaw(FutureFile);

        using (File.Open(ReplaceFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Contains("will not be saved", FavoritesStore.Replacements.TakeWarning());

            FavoritesStore.Replacements.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "mine");
            FavoritesStore.Queries.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "still saved");
        }

        Assert.Equal(FutureFile, File.ReadAllText(ReplaceFile));
        Assert.True(File.Exists(QueryFile));
    }

    [Fact]
    public void TakeWarning_WithoutAPriorRead_StillReportsANewerFile()
    {
        WriteRaw(FutureFile);

        Assert.NotNull(FavoritesStore.Replacements.TakeWarning());
    }

    private void WriteRaw(string contents)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(ReplaceFile, contents, System.Text.Encoding.UTF8);
        Reload();
    }
}
