using System.Collections.Generic;

using RoslynQuery.Storage;

namespace RoslynQuery.Favorites;

/// <summary>Version 1 of <c>replace-favorites.tsv</c>. Its own chain: a query favorites version 2 never reaches here.</summary>
internal sealed class ReplaceFavoritesVersion1 : FormatVersion<IReadOnlyList<FavoriteEntry>>
{
    public override int Version => 1;

    protected override IReadOnlyList<FavoriteEntry> Parse(IReadOnlyList<string> rows) => FavoritesRows.Parse(rows);

    public string Row(FavoriteEntry entry) => FavoritesRows.Write(entry);
}

/// <summary>Starred replacement expressions.</summary>
internal sealed class ReplaceFavoritesFormat : FavoritesFormat
{
    private static readonly ReplaceFavoritesVersion1 Latest = new ReplaceFavoritesVersion1();

    public override string Name => "roslynquery-replace-favorites";

    protected override FormatVersion<IReadOnlyList<FavoriteEntry>> Current => Latest;

    protected override string Row(FavoriteEntry entry) => Latest.Row(entry);
}
