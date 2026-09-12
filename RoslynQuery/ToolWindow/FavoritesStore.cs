using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using RoslynQuery.Query;

namespace RoslynQuery.ToolWindow;

/// <summary>
/// Starred predicates, persisted under %LocalAppData%. Keys match <see cref="PredicateCompiler"/>'s
/// cache exactly, so a favorite and its compiled entry are the same sidebar row. The name is a label
/// only and stays out of the key. <see cref="FavoritesFormat"/> owns the file's shape.
/// </summary>
internal static class FavoritesStore
{
    private static readonly object Gate = new object();
    private static List<Entry> _entries;
    private static string _directoryOverride;
    private static string _warning;
    private static bool _refuseToWrite;

    /// <summary>One starred predicate. <see cref="Name"/> is null when the row shows the predicate itself.</summary>
    internal readonly struct Entry : IEquatable<Entry>
    {
        public Entry(TargetKind kind, PredicateMode mode, string text, string name)
        {
            Kind = kind;
            Mode = mode;
            Text = text;
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }

        public TargetKind Kind { get; }
        public PredicateMode Mode { get; }
        public string Text { get; }
        public string Name { get; }

        public bool Equals(Entry other) =>
            Kind == other.Kind
            && Mode == other.Mode
            && string.Equals(Text, other.Text, StringComparison.Ordinal)
            && string.Equals(Name, other.Name, StringComparison.Ordinal);

        public override bool Equals(object obj) => obj is Entry other && Equals(other);

        public override int GetHashCode() =>
            (((int)Kind * 397 ^ (int)Mode) * 397 ^ (Text?.GetHashCode() ?? 0)) * 397 ^ (Name?.GetHashCode() ?? 0);

        public override string ToString() =>
            Name is null ? Kind + " " + Mode + " " + Text : Kind + " " + Mode + " " + Text + " as " + Name;
    }

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
                _warning = null;
                _refuseToWrite = false;
            }
        }
    }

    /// <summary>
    /// A complaint the window should show once, or null. Reading it clears it, so the same displaced file is
    /// only ever reported once per load.
    /// </summary>
    public static string TakeWarning()
    {
        lock (Gate)
        {
            var warning = _warning;
            _warning = null;

            return warning;
        }
    }

    /// <summary>Most-recently-starred first.</summary>
    public static IReadOnlyList<Entry> All
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

    /// <summary>The label on a starred row, or null when it has none or is not starred.</summary>
    public static string NameOf(TargetKind kind, PredicateMode mode, string text)
    {
        lock (Gate)
        {
            var entries = Load();
            var existing = IndexOf(entries, kind, mode, text);

            return existing < 0 ? null : entries[existing].Name;
        }
    }

    public static void Add(TargetKind kind, PredicateMode mode, string text, string name = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        lock (Gate)
        {
            var entries = Load();
            var existing = IndexOf(entries, kind, mode, text);
            if (existing >= 0) entries.RemoveAt(existing);

            entries.Insert(0, new Entry(kind, mode, text, name));
            Save(entries);
        }
    }

    /// <summary>Relabels a starred row in place, keeping its position. An empty name clears the label.</summary>
    public static void Rename(TargetKind kind, PredicateMode mode, string text, string name)
    {
        lock (Gate)
        {
            var entries = Load();
            var existing = IndexOf(entries, kind, mode, text);
            if (existing < 0) return;

            entries[existing] = new Entry(kind, mode, entries[existing].Text, name);
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

    private static int IndexOf(List<Entry> entries, TargetKind kind, PredicateMode mode, string text)
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

    private static List<Entry> Load()
    {
        if (_entries != null) return _entries;

        try
        {
            var path = FilePath;

            if (!File.Exists(path))
            {
                _entries = [];
                return _entries;
            }

            var (stamped, entries) = FavoritesFormat.Read(File.ReadAllLines(path, Encoding.UTF8));

            if (stamped > FavoritesFormat.CurrentVersion) MoveAside(path, stamped);

            _entries = entries;
        }
        catch (Exception)
        {
            // An unreadable file must never be what breaks the sidebar.
            _entries = [];
        }

        return _entries;
    }

    /// <summary>
    /// Keeps a file a newer extension wrote, by renaming it rather than letting the next star overwrite
    /// favorites this version cannot read. If it cannot be moved, nothing is written at all this session:
    /// leaving that file untouched matters more than persisting a star.
    /// </summary>
    private static void MoveAside(string path, int stampedVersion)
    {
        var backup = path + ".v" + stampedVersion + ".bak";
        for (var i = 2; File.Exists(backup); i++) backup = path + ".v" + stampedVersion + "-" + i + ".bak";

        try
        {
            File.Move(path, backup);
        }
        catch (Exception ex)
        {
            _refuseToWrite = true;
            _warning =
                "Your favorites were written by a newer version of RoslynQuery (file format version "
                + stampedVersion + "; this version understands " + FavoritesFormat.CurrentVersion + "), and that file could not be moved aside:"
                + "\r\n\r\n" + ex.Message
                + "\r\n\r\nFavorites will not be saved this session, so the file stays as it is.";

            return;
        }

        _warning =
            "Your favorites were written by a newer version of RoslynQuery (file format version "
            + stampedVersion + "; this version understands " + FavoritesFormat.CurrentVersion + "), so they could not be read."
            + "\r\n\r\nThat file has been kept as:\r\n\r\n" + backup
            + "\r\n\r\nFavorites start empty from here. Delete the new file and rename that one back to return to it.";
    }

    private static void Save(List<Entry> entries)
    {
        if (_refuseToWrite) return;

        try
        {
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            // Staged then copied over: a crash mid-write would otherwise truncate the live file and
            // take every favorite with it, not just the one being written.
            var temp = path + ".tmp";
            File.WriteAllText(temp, FavoritesFormat.Write(entries), Encoding.UTF8);
            File.Copy(temp, path, overwrite: true);
            File.Delete(temp);
        }
        catch (Exception)
        {
        }
    }
}
