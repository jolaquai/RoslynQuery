using System;
using System.Linq;

using RoslynQuery.Favorites;
using RoslynQuery.Query;

using Xunit;

namespace RoslynQuery.Tests;

public sealed class FavoritesFormatTests
{
    private static readonly FavoriteEntry[] Sample =
    [
        new FavoriteEntry(TargetKind.SyntaxNode, PredicateMode.Expression, "n != null", null),
        new FavoriteEntry(TargetKind.SyntaxToken, PredicateMode.Body, "return t.Text == \"a\tb\";", "tab\tname"),
        new FavoriteEntry(TargetKind.Operation, PredicateMode.Expression, "@\"a\r\nb\"", "back\\slash")
    ];

    public static TheoryData<string, string> Formats => new TheoryData<string, string>
    {
        { nameof(QueryFavoritesFormat), "roslynquery-favorites" },
        { nameof(ReplaceFavoritesFormat), "roslynquery-replace-favorites" }
    };

    private static FavoritesFormat Create(string format) => format switch
    {
        nameof(QueryFavoritesFormat) => new QueryFavoritesFormat(),
        nameof(ReplaceFavoritesFormat) => new ReplaceFavoritesFormat(),
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    private static string[] Lines(string text) => text.Split(["\r\n"], StringSplitOptions.None);

    [Theory]
    [MemberData(nameof(Formats))]
    public void Write_StampsTheNameAtVersionOne(string format, string stamp)
    {
        var sut = Create(format);

        Assert.Equal(stamp, sut.Name);
        Assert.Equal(1, sut.CurrentVersion);
        Assert.Equal(stamp + "\t1", Lines(sut.Write(Sample))[0]);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Rows_RoundTrip(string format, string stamp)
    {
        var sut = Create(format);

        var (stamped, entries) = sut.Read(Lines(sut.Write(Sample)));

        Assert.Equal(1, stamped);
        Assert.Equal(stamp, sut.Name);
        Assert.Equal(Sample, entries);
    }

    [Fact]
    public void EachFormat_RefusesTheOthersStamp()
    {
        var query = new QueryFavoritesFormat();
        var replace = new ReplaceFavoritesFormat();

        var (fromQuery, queryEntries) = replace.Read(Lines(query.Write(Sample)));
        var (fromReplace, replaceEntries) = query.Read(Lines(replace.Write(Sample)));

        Assert.Equal(0, fromQuery);
        Assert.Empty(queryEntries);
        Assert.Equal(0, fromReplace);
        Assert.Empty(replaceEntries);
    }

    [Fact]
    public void BothFormats_ShareTheRowShape()
    {
        var query = Lines(new QueryFavoritesFormat().Write(Sample));
        var replace = Lines(new ReplaceFavoritesFormat().Write(Sample));

        Assert.Equal(query.Skip(1), replace.Skip(1));
    }

    [Fact]
    public void QueryFormat_StillReadsAnExistingVersionOneFile()
    {
        var (stamped, entries) = new QueryFavoritesFormat().Read(
            ["roslynquery-favorites\t1", "SyntaxNode\tExpression\tn != null\t", "Operation\tBody\treturn true;\tAlways"]);

        Assert.Equal(1, stamped);
        Assert.Equal(
            [new FavoriteEntry(TargetKind.SyntaxNode, PredicateMode.Expression, "n != null", null),
             new FavoriteEntry(TargetKind.Operation, PredicateMode.Body, "return true;", "Always")],
            entries);
    }
}
