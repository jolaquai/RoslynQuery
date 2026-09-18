using System;
using System.Collections.Generic;

using RoslynQuery.Query;
using RoslynQuery.Storage;

namespace RoslynQuery.ToolWindow;

/// <summary>
/// The on-disk shape of <c>favorites.tsv</c>. Version 1 is what this extension writes and the only version
/// there has ever been; a version 2 would subclass <see cref="FormatVersion{TModel, TPreviousModel}"/>,
/// parse its own rows, and upgrade version 1's model, leaving this class untouched.
/// </summary>
internal sealed class FavoritesVersion1 : FormatVersion<IReadOnlyList<FavoritesStore.Entry>>
{
    private const int Fields = 4;

    public override int Version => 1;

    protected override IReadOnlyList<FavoritesStore.Entry> Parse(IReadOnlyList<string> rows)
    {
        var entries = new List<FavoritesStore.Entry>(rows.Count);

        foreach (var row in rows)
        {
            var fields = TabSeparated.FieldsOfLength(row, Fields);
            if (fields is null) continue;
            if (!Enum.TryParse<TargetKind>(fields[0], out var kind) || !Enum.IsDefined(typeof(TargetKind), kind)) continue;
            if (!Enum.TryParse<PredicateMode>(fields[1], out var mode) || !Enum.IsDefined(typeof(PredicateMode), mode)) continue;

            entries.Add(new FavoritesStore.Entry(kind, mode, fields[2], fields[3]));
        }

        return entries;
    }

    /// <summary>Writing lives on the concrete current version, so an older version cannot be asked to produce a file.</summary>
    public string Row(FavoritesStore.Entry entry) =>
        TabSeparated.Row(entry.Kind.ToString(), entry.Mode.ToString(), entry.Text, entry.Name);
}

internal static class FavoritesFormat
{
    public const string Name = "roslynquery-favorites";

    /// <summary>The version this extension writes, and the top of the upgrade chain it can read.</summary>
    public static readonly FavoritesVersion1 Current = new FavoritesVersion1();

    public static int CurrentVersion => Current.Version;

    /// <summary>
    /// Whatever version the file was stamped with, brought up to <see cref="Current"/>. An unreadable stamp
    /// reads as empty rather than throwing, because a corrupt file must never be what breaks the sidebar.
    /// The stamp comes back too, so a caller can tell "empty" from "written by a newer extension".
    /// </summary>
    public static (int StampedVersion, List<FavoritesStore.Entry> Entries) Read(IReadOnlyList<string> lines)
    {
        var (version, rows) = VersionedFile.Read(Name, lines);

        return (version, Valid(Current.Read(version, rows)));
    }

    public static string Write(IReadOnlyList<FavoritesStore.Entry> entries)
    {
        var rows = new string[entries.Count];
        for (var i = 0; i < entries.Count; i++) rows[i] = Current.Row(entries[i]);

        return VersionedFile.Write(Name, Current.Version, rows);
    }

    /// <summary>
    /// Rules every version shares, applied after the upgrade chain rather than inside each parser: a row needs
    /// a predicate, and one key appears once. Only a hand-edited file can break either, since Write cannot.
    /// </summary>
    private static List<FavoritesStore.Entry> Valid(IReadOnlyList<FavoritesStore.Entry> entries)
    {
        var result = new List<FavoritesStore.Entry>(entries.Count);
        var seen = new HashSet<(TargetKind, PredicateMode, string)>();

        foreach (var entry in entries)
        {
            if (!string.IsNullOrWhiteSpace(entry.Text) && seen.Add((entry.Kind, entry.Mode, entry.Text)))
                result.Add(entry);
        }

        return result;
    }
}
