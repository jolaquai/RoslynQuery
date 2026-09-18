using System.Collections.Generic;

using RoslynQuery.Storage;

namespace RoslynQuery.Favorites;

/// <summary>
/// Version 1 of <c>favorites.tsv</c>. A version 2 would subclass <see cref="FormatVersion{TModel, TPreviousModel}"/>,
/// parse its own rows, and upgrade this version's model, leaving this class untouched.
/// </summary>
internal sealed class QueryFavoritesVersion1 : FormatVersion<IReadOnlyList<FavoriteEntry>>
{
    public override int Version => 1;

    protected override IReadOnlyList<FavoriteEntry> Parse(IReadOnlyList<string> rows) => FavoritesRows.Parse(rows);

    public string Row(FavoriteEntry entry) => FavoritesRows.Write(entry);
}

/// <summary>Starred predicates.</summary>
internal sealed class QueryFavoritesFormat : FavoritesFormat
{
    private static readonly QueryFavoritesVersion1 Latest = new QueryFavoritesVersion1();

    public override string Name => "roslynquery-favorites";

    protected override FormatVersion<IReadOnlyList<FavoriteEntry>> Current => Latest;

    protected override string Row(FavoriteEntry entry) => Latest.Row(entry);
}
