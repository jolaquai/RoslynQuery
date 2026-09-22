using System;
using System.Collections.Generic;

using RoslynQuery.Query;
using RoslynQuery.Storage;

namespace RoslynQuery.Favorites;

/// <summary>
/// What every favorites file shares: the stamped header, the upgrade chain entry point and the validity rules.
/// A concrete format owns its stamp and its own version chain, so a new version on one file never touches another.
/// </summary>
internal abstract class FavoritesFormat
{
    public abstract string Name { get; }

    /// <summary>The version this extension writes, and the top of the upgrade chain it can read.</summary>
    protected abstract FormatVersion<IReadOnlyList<FavoriteEntry>> Current { get; }

    /// <summary>Writing lives on the concrete current version, so an older version cannot be asked to produce a file.</summary>
    protected abstract string Row(FavoriteEntry entry);

    public int CurrentVersion => Current.Version;

    /// <summary>
    /// Whatever version the file was stamped with, brought up to <see cref="Current"/>. An unreadable stamp
    /// reads as empty rather than throwing, because a corrupt file must never be what breaks the sidebar.
    /// The stamp comes back too, so a caller can tell "empty" from "written by a newer extension".
    /// </summary>
    public (int StampedVersion, List<FavoriteEntry> Entries) Read(IReadOnlyList<string> lines)
    {
        var (version, rows) = VersionedFile.Read(Name, lines);

        return (version, Valid(Current.Read(version, rows)));
    }

    public string Write(IReadOnlyList<FavoriteEntry> entries)
    {
        var rows = new string[entries.Count];
        for (var i = 0; i < entries.Count; i++) rows[i] = Row(entries[i]);

        return VersionedFile.Write(Name, CurrentVersion, rows);
    }

    /// <summary>
    /// Rules every version shares, applied after the upgrade chain rather than inside each parser: a row needs
    /// an expression, and one key appears once. Only a hand-edited file can break either, since Write cannot.
    /// </summary>
    private static List<FavoriteEntry> Valid(IReadOnlyList<FavoriteEntry> entries)
    {
        var result = new List<FavoriteEntry>(entries.Count);
        var seen = new HashSet<(TargetKind, PredicateMode, string)>();

        foreach (var entry in entries)
        {
            if (!string.IsNullOrWhiteSpace(entry.Text) && seen.Add((entry.Kind, entry.Mode, entry.Text)))
                result.Add(entry);
        }

        return result;
    }
}

/// <summary>The four-field row both version 1 formats happen to share. Sharing the row shape does not share a version chain.</summary>
internal static class FavoritesRows
{
    private const int Fields = 4;

    public static IReadOnlyList<FavoriteEntry> Parse(IReadOnlyList<string> rows)
    {
        var entries = new List<FavoriteEntry>(rows.Count);

        foreach (var row in rows)
        {
            var fields = TabSeparated.FieldsOfLength(row, Fields);
            if (fields is null) continue;
            if (!Enum.TryParse<TargetKind>(fields[0], out var kind) || !Enum.IsDefined(typeof(TargetKind), kind)) continue;
            if (!Enum.TryParse<PredicateMode>(fields[1], out var mode) || !Enum.IsDefined(typeof(PredicateMode), mode)) continue;

            entries.Add(new FavoriteEntry(kind, mode, fields[2], fields[3]));
        }

        return entries;
    }

    public static string Write(FavoriteEntry entry) =>
        TabSeparated.Row(entry.Kind.ToString(), entry.Mode.ToString(), entry.Text, entry.Name);
}
