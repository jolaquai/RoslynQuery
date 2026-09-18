using System;
using System.IO;
using System.Linq;

using RoslynQuery.Favorites;
using RoslynQuery.Query;
using RoslynQuery.Replace;
using RoslynQuery.ToolWindow;

using Xunit;

namespace RoslynQuery.Tests;

[Collection(FavoritesCollection.Name)]
public sealed class HistoryListTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "RoslynQueryTests", Guid.NewGuid().ToString("N"));

    public HistoryListTests() => FavoritesStore.DirectoryOverride = _directory;

    public void Dispose()
    {
        FavoritesStore.DirectoryOverride = null;
        try
        { Directory.Delete(_directory, recursive: true); }
        catch (Exception) { }
    }

    private static (TargetKind Kind, PredicateMode Mode, string Text) Key(string text) => (TargetKind.SyntaxNode, PredicateMode.Expression, text);

    private static HistoryItem Row(HistoryList list, string text) => list.Items.Single(i => i.Text == text);

    [Fact]
    public void Refresh_PinsFavoritesAboveTheSnapshot_AndDedupesOnTheKey()
    {
        FavoritesStore.Queries.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "starred", "Named");
        var list = new HistoryList(FavoritesStore.Queries);

        list.Refresh([Key("cached"), Key("starred")]);

        Assert.Equal(["starred", "cached"], list.Items.Select(i => i.Text));
        Assert.Equal([true, false], list.Items.Select(i => i.IsFavorite));
        Assert.Equal("Named", list.Items[0].Name);
    }

    [Fact]
    public void Refresh_SetsEveryRowsOwner()
    {
        FavoritesStore.Queries.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "starred");
        var list = new HistoryList(FavoritesStore.Queries);

        list.Refresh([Key("cached")]);

        Assert.All(list.Items, i => Assert.Same(list, i.Owner));
    }

    [Fact]
    public void ToggleFavorite_StarsAndUnstarsInItsOwnStore()
    {
        var list = new HistoryList(FavoritesStore.Replacements);
        list.Refresh([Key("\"x\"")]);
        var row = Row(list, "\"x\"");

        list.ToggleFavorite(row);

        Assert.True(row.IsFavorite);
        Assert.True(FavoritesStore.Replacements.Contains(TargetKind.SyntaxNode, PredicateMode.Expression, "\"x\""));
        Assert.Empty(FavoritesStore.Queries.All);

        list.ToggleFavorite(row);

        Assert.False(row.IsFavorite);
        Assert.Empty(FavoritesStore.Replacements.All);
    }

    [Fact]
    public void DroppingAStarredRow_UnstarsAndHidesIt()
    {
        FavoritesStore.Queries.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "a");
        var list = new HistoryList(FavoritesStore.Queries);
        list.Refresh([Key("a")]);

        list.Drop(Row(list, "a"));

        Assert.Empty(list.Items);
        Assert.Empty(FavoritesStore.Queries.All);

        list.Refresh([Key("a")]);

        Assert.Empty(list.Items);
    }

    [Fact]
    public void DroppingAnUnstarredRow_HidesOnlyThatRow()
    {
        var list = new HistoryList(FavoritesStore.Queries);
        list.Refresh([Key("a"), Key("b")]);

        list.Drop(Row(list, "a"));
        list.Refresh([Key("a"), Key("b")]);

        Assert.Equal(["b"], list.Items.Select(i => i.Text));
    }

    [Fact]
    public void Unhide_BringsADroppedRowBackOnTheNextRefresh()
    {
        var list = new HistoryList(FavoritesStore.Queries);
        list.Refresh([Key("a")]);
        list.Drop(Row(list, "a"));

        list.Unhide(Key("a"));
        list.Refresh([Key("a")]);

        Assert.Equal(["a"], list.Items.Select(i => i.Text));
    }

    /// <summary>The window unhides by the compiler's key, so a row must be keyed exactly the way the compiler keys it.</summary>
    [Fact]
    public void Unhide_ByTheReplaceCompilersKey_MatchesARowFromItsSnapshotShape()
    {
        var key = ReplaceCompiler.KeyFor(TargetKind.SyntaxToken, "t.Text   +   \"x\"");
        var list = new HistoryList(FavoritesStore.Replacements);
        list.Refresh([key]);
        list.Drop(list.Items.Single());

        list.Unhide(ReplaceCompiler.KeyFor(TargetKind.SyntaxToken, "t.Text+\"x\""));
        list.Refresh([key]);

        Assert.Single(list.Items);
    }

    [Fact]
    public void Hiding_IsPerList()
    {
        var queries = new HistoryList(FavoritesStore.Queries);
        var replacements = new HistoryList(FavoritesStore.Replacements);
        queries.Refresh([Key("a")]);

        queries.Drop(Row(queries, "a"));
        replacements.Refresh([Key("a")]);

        Assert.Single(replacements.Items);
    }

    [Fact]
    public void RenamingAStarredRow_Persists()
    {
        FavoritesStore.Replacements.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "\"x\"");
        var list = new HistoryList(FavoritesStore.Replacements);
        list.Refresh([]);
        var row = Row(list, "\"x\"");

        row.BeginEdit();
        row.EditText = "Ex";
        list.CommitRename(row);
        FavoritesStore.DirectoryOverride = _directory;

        Assert.Equal("Ex", row.Name);
        Assert.False(row.IsEditing);
        Assert.Equal("Ex", FavoritesStore.Replacements.NameOf(TargetKind.SyntaxNode, PredicateMode.Expression, "\"x\""));
    }

    [Fact]
    public void RenamingAnUnstarredRow_StaysInMemoryOnly()
    {
        var list = new HistoryList(FavoritesStore.Queries);
        list.Refresh([Key("a")]);
        var row = Row(list, "a");

        row.BeginEdit();
        row.EditText = "Ay";
        list.CommitRename(row);

        Assert.Equal("Ay", row.Name);
        Assert.Empty(FavoritesStore.Queries.All);
        Assert.False(File.Exists(Path.Combine(_directory, "favorites.tsv")));
    }

    /// <summary>Escape ends the edit first, then collapsing the editor raises LostKeyboardFocus, which must not commit.</summary>
    [Fact]
    public void CommitRename_AfterTheEditEnded_IsANoOp()
    {
        FavoritesStore.Queries.Add(TargetKind.SyntaxNode, PredicateMode.Expression, "a", "Kept");
        var list = new HistoryList(FavoritesStore.Queries);
        list.Refresh([]);
        var row = Row(list, "a");

        row.BeginEdit();
        row.EditText = "Abandoned";
        row.IsEditing = false;
        list.CommitRename(row);

        Assert.Equal("Kept", row.Name);
        Assert.Equal("Kept", FavoritesStore.Queries.NameOf(TargetKind.SyntaxNode, PredicateMode.Expression, "a"));
    }
}
