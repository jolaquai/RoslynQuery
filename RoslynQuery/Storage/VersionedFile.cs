using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RoslynQuery.Storage;

/// <summary>
/// A line-based file whose first line stamps the format name and its version, so a reader knows which
/// <see cref="FormatVersion{TModel}"/> wrote it before trying to parse anything.
/// </summary>
internal static class VersionedFile
{
    public static string Header(string formatName, int version) =>
        formatName + "\t" + version.ToString(CultureInfo.InvariantCulture);

    /// <summary>The stamped version and the rows below it. Version 0 means the stamp is missing, foreign or unreadable.</summary>
    public static (int Version, IReadOnlyList<string> Rows) Read(string formatName, IReadOnlyList<string> lines)
    {
        if (lines is null || lines.Count == 0) return (0, []);

        var stamp = lines[0].Split('\t');
        if (stamp.Length != 2 || !string.Equals(stamp[0], formatName, StringComparison.Ordinal)) return (0, []);
        if (!int.TryParse(stamp[1], NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version <= 0) return (0, []);

        var rows = new string[lines.Count - 1];
        for (var i = 1; i < lines.Count; i++) rows[i - 1] = lines[i];

        return (version, rows);
    }

    public static string Write(string formatName, int version, IEnumerable<string> rows)
    {
        var sb = new StringBuilder();
        sb.Append(Header(formatName, version)).Append("\r\n");
        foreach (var row in rows) sb.Append(row).Append("\r\n");

        return sb.ToString();
    }
}
