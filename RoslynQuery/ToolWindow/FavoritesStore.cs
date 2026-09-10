using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using RoslynQuery.Query;

namespace RoslynQuery.ToolWindow;

/// <summary>
/// Starred predicates, persisted under %LocalAppData%. Keys match <see cref="PredicateCompiler"/>'s
/// cache exactly, so a favorite and its compiled entry are the same sidebar row.
/// </summary>
internal static class FavoritesStore
{
    private const string Header = "roslynquery-favorites\t1";

    private static readonly object Gate = new object();
    private static List<(TargetKind Kind, PredicateMode Mode, string Text)> _entries;
    private static string _directoryOverride;

    /// <summary>Test seam: redirects the store off the real user profile. Null uses %LocalAppData%.</summary>
    internal static string DirectoryOverride
    {
        get => _directoryOverride;
        set
        {
            lock (Gate)
            {
                _directoryOverride = value;
                _entries = null;
            }
        }
    }

    /// <summary>Most-recently-starred first.</summary>
    public static IReadOnlyList<(TargetKind Kind, PredicateMode Mode, string Text)> All
    {
        get
        {
            lock (Gate) return Load().ToArray();
        }
    }

    public static bool Contains(TargetKind kind, PredicateMode mode, string text)
    {
        lock (Gate) return IndexOf(Load(), kind, mode, text) >= 0;
    }

    public static void Add(TargetKind kind, PredicateMode mode, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        lock (Gate)
        {
            var entries = Load();
            var existing = IndexOf(entries, kind, mode, text);
            if (existing >= 0) entries.RemoveAt(existing);

            entries.Insert(0, (kind, mode, text));
            Save(entries);
        }
    }

    public static void Remove(TargetKind kind, PredicateMode mode, string text)
    {
        lock (Gate)
        {
            var entries = Load();
            var existing = IndexOf(entries, kind, mode, text);
            if (existing < 0) return;

            entries.RemoveAt(existing);
            Save(entries);
        }
    }

    private static int IndexOf(List<(TargetKind Kind, PredicateMode Mode, string Text)> entries, TargetKind kind, PredicateMode mode, string text)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry.Kind == kind && entry.Mode == mode && string.Equals(entry.Text, text, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    private static string FilePath => Path.Combine(
        _directoryOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RoslynQuery"),
        "favorites.tsv");

    private static List<(TargetKind Kind, PredicateMode Mode, string Text)> Load()
    {
        if (_entries != null) return _entries;

        _entries = [];
        try
        {
            var path = FilePath;
            if (!File.Exists(path)) return _entries;

            var lines = File.ReadAllLines(path, Encoding.UTF8);
            if (lines.Length == 0 || !string.Equals(lines[0], Header, StringComparison.Ordinal)) return _entries;

            // Set rather than a scan per line: Save never writes a duplicate, so this only guards a
            // hand-edited file, and it must not cost O(n^2) to do it.
            var seen = new HashSet<(TargetKind, PredicateMode, string)>();
            for (var i = 1; i < lines.Length; i++)
            {
                var parts = lines[i].Split('\t');
                if (parts.Length != 3) continue;
                if (!Enum.TryParse<TargetKind>(parts[0], out var kind) || !Enum.IsDefined(typeof(TargetKind), kind)) continue;
                if (!Enum.TryParse<PredicateMode>(parts[1], out var mode) || !Enum.IsDefined(typeof(PredicateMode), mode)) continue;

                var text = Unescape(parts[2]);
                if (!string.IsNullOrWhiteSpace(text) && seen.Add((kind, mode, text)))
                    _entries.Add((kind, mode, text));
            }
        }
        catch (Exception)
        {
            // An unreadable or corrupt file must never be what breaks the sidebar.
        }

        return _entries;
    }

    private static void Save(List<(TargetKind Kind, PredicateMode Mode, string Text)> entries)
    {
        try
        {
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            var sb = new StringBuilder();
            sb.Append(Header).Append("\r\n");
            foreach (var (kind, mode, text) in entries)
                sb.Append(kind).Append('\t').Append(mode).Append('\t').Append(Escape(text)).Append("\r\n");

            // Staged then copied over: a crash mid-write would otherwise truncate the live file and
            // take every favorite with it, not just the one being written.
            var temp = path + ".tmp";
            File.WriteAllText(temp, sb.ToString(), Encoding.UTF8);
            File.Copy(temp, path, overwrite: true);
            File.Delete(temp);
        }
        catch (Exception)
        {
        }
    }

    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\r': sb.Append("\\r"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }

        return sb.ToString();
    }

    private static string Unescape(string text)
    {
        if (text.IndexOf('\\') < 0) return text;

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\' || i + 1 == text.Length)
            {
                sb.Append(text[i]);
                continue;
            }

            switch (text[++i])
            {
                case 'r': sb.Append('\r'); break;
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case '\\': sb.Append('\\'); break;
                default: sb.Append('\\').Append(text[i]); break;
            }
        }

        return sb.ToString();
    }
}
