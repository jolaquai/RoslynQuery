using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using RoslynQuery.Query;

namespace RoslynQuery.Favorites;

/// <summary>
/// Starred expressions, persisted under %LocalAppData%. Keys match the owning compiler's cache exactly, so a
/// favorite and its compiled entry are the same sidebar row. The name is a label only and stays out of the key.
/// The <see cref="FavoritesFormat"/> it is built with owns the file's shape.
/// </summary>
internal sealed class FavoritesStore
{
    private static readonly List<FavoritesStore> Instances = [];
    private static string _directoryOverride;

    public static readonly FavoritesStore Queries = new FavoritesStore("favorites.tsv", new QueryFavoritesFormat());
    public static readonly FavoritesStore Replacements = new FavoritesStore("replace-favorites.tsv", new ReplaceFavoritesFormat());

    private readonly object _gate = new object();
    private readonly string _fileName;
    private readonly FavoritesFormat _format;
    private List<FavoriteEntry> _entries;
    private string _warning;
    private bool _refuseToWrite;

    public FavoritesStore(string fileName, FavoritesFormat format)
    {
        _fileName = fileName;
        _format = format;

        lock (Instances) Instances.Add(this);
    }

    /// <summary>
    /// Test seam: redirects every store off the real user profile. Null uses %LocalAppData%. Assigning always
    /// drops what every store holds in memory, so the next read comes back off disk.
    /// </summary>
    internal static string DirectoryOverride
    {
        get => _directoryOverride;
        set
        {
            lock (Instances)
            {
                _directoryOverride = value;
                foreach (var store in Instances) store.Reset();
            }
        }
    }

    private void Reset()
    {
        lock (_gate)
        {
            _entries = null;
            _warning = null;
            _refuseToWrite = false;
        }
    }

    /// <summary>
    /// A complaint the window should show once, or null. Reading it clears it, so the same displaced file is
    /// only ever reported once per load.
    /// </summary>
    public string TakeWarning()
    {
        lock (_gate)
        {
            Load();

            var warning = _warning;
            _warning = null;

            return warning;
        }
    }

    /// <summary>Most-recently-starred first.</summary>
    public IReadOnlyList<FavoriteEntry> All
    {
        get
        {
            lock (_gate) return Load().ToArray();
        }
    }

    public bool Contains(TargetKind kind, PredicateMode mode, string text)
    {
        lock (_gate) return IndexOf(Load(), kind, mode, text) >= 0;
    }

    /// <summary>The label on a starred row, or null when it has none or is not starred.</summary>
    public string NameOf(TargetKind kind, PredicateMode mode, string text)
    {
        lock (_gate)
        {
            var entries = Load();
            var existing = IndexOf(entries, kind, mode, text);

            return existing < 0 ? null : entries[existing].Name;
        }
    }

    public void Add(TargetKind kind, PredicateMode mode, string text, string name = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        lock (_gate)
        {
            var entries = Load();
            var existing = IndexOf(entries, kind, mode, text);
            if (existing >= 0) entries.RemoveAt(existing);

            entries.Insert(0, new FavoriteEntry(kind, mode, text, name));
            Save(entries);
        }
    }

    /// <summary>Relabels a starred row in place, keeping its position. An empty name clears the label.</summary>
    public void Rename(TargetKind kind, PredicateMode mode, string text, string name)
    {
        lock (_gate)
        {
            var entries = Load();
            var existing = IndexOf(entries, kind, mode, text);
            if (existing < 0) return;

            entries[existing] = new FavoriteEntry(kind, mode, entries[existing].Text, name);
            Save(entries);
        }
    }

    public void Remove(TargetKind kind, PredicateMode mode, string text)
    {
        lock (_gate)
        {
            var entries = Load();
            var existing = IndexOf(entries, kind, mode, text);
            if (existing < 0) return;

            entries.RemoveAt(existing);
            Save(entries);
        }
    }

    private static int IndexOf(List<FavoriteEntry> entries, TargetKind kind, PredicateMode mode, string text)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry.Kind == kind && entry.Mode == mode && string.Equals(entry.Text, text, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    private string FilePath => Path.Combine(
        _directoryOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RoslynQuery"),
        _fileName);

    private List<FavoriteEntry> Load()
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

            var (stamped, entries) = _format.Read(File.ReadAllLines(path, Encoding.UTF8));

            if (stamped > _format.CurrentVersion) MoveAside(path, stamped);

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
    private void MoveAside(string path, int stampedVersion)
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
                + stampedVersion + "; this version understands " + _format.CurrentVersion + "), and that file could not be moved aside:"
                + "\r\n\r\n" + ex.Message
                + "\r\n\r\nFavorites will not be saved this session, so the file stays as it is.";

            return;
        }

        _warning =
            "Your favorites were written by a newer version of RoslynQuery (file format version "
            + stampedVersion + "; this version understands " + _format.CurrentVersion + "), so they could not be read."
            + "\r\n\r\nThat file has been kept as:\r\n\r\n" + backup
            + "\r\n\r\nFavorites start empty from here. Delete the new file and rename that one back to return to it.";
    }

    private void Save(List<FavoriteEntry> entries)
    {
        if (_refuseToWrite) return;

        try
        {
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            // Staged then copied over: a crash mid-write would otherwise truncate the live file and
            // take every favorite with it, not just the one being written.
            var temp = path + ".tmp";
            File.WriteAllText(temp, _format.Write(entries), Encoding.UTF8);
            File.Copy(temp, path, overwrite: true);
            File.Delete(temp);
        }
        catch (Exception)
        {
        }
    }
}
