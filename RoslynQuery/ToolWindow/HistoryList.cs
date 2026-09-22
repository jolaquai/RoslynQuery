using System.Collections.Generic;
using System.Collections.ObjectModel;

using RoslynQuery.Favorites;
using RoslynQuery.Query;

namespace RoslynQuery.ToolWindow;

/// <summary>
/// One sidebar history section: its rows, the store behind its stars, and the rows the user dropped.
/// Single-threaded: the window only ever touches it from the UI thread.
/// </summary>
internal sealed class HistoryList(FavoritesStore store)
{
    // Session-scoped on purpose: the compiler cache behind these rows is per-process too.
    private readonly HashSet<(TargetKind Kind, PredicateMode Mode, string Text)> _hidden = [];

    public ObservableCollection<HistoryItem> Items { get; } = [];

    public FavoritesStore Store { get; } = store;

    /// <summary>Favorites pinned above the live cache snapshot, deduped on the shared cache key.</summary>
    public void Refresh(IReadOnlyList<(TargetKind Kind, PredicateMode Mode, string Text)> snapshot)
    {
        Items.Clear();
        var seen = new HashSet<(TargetKind, PredicateMode, string)>(_hidden);

        foreach (var entry in Store.All)
        {
            if (seen.Add((entry.Kind, entry.Mode, entry.Text)))
                Items.Add(new HistoryItem(entry.Kind, entry.Mode, entry.Text, isFavorite: true, name: entry.Name, owner: this));
        }

        foreach (var (kind, mode, text) in snapshot)
        {
            if (seen.Add((kind, mode, text)))
                Items.Add(new HistoryItem(kind, mode, text, owner: this));
        }
    }

    public void ToggleFavorite(HistoryItem item)
    {
        item.IsFavorite = !item.IsFavorite;
        if (item.IsFavorite) Store.Add(item.Kind, item.Mode, item.Text, item.Name);
        else Store.Remove(item.Kind, item.Mode, item.Text);
    }

    /// <summary>A name matching the expression, or an emptied box, clears the label rather than storing it.</summary>
    public void CommitRename(HistoryItem item)
    {
        // Escape already ended the edit, and collapsing the editor then raises LostKeyboardFocus.
        if (!item.IsEditing) return;

        item.CommitEdit();

        if (item.IsFavorite) Store.Rename(item.Kind, item.Mode, item.Text, item.Name);
    }

    /// <summary>
    /// Never touches the compiler's cache: on net472 the emitted assembly cannot be unloaded, so evicting it
    /// reclaims nothing and re-running the same text would leak a second one.
    /// </summary>
    public void Drop(HistoryItem item)
    {
        // Unstarred as well as hidden: a starred row would otherwise come back on the next refresh.
        if (item.IsFavorite) Store.Remove(item.Kind, item.Mode, item.Text);

        _hidden.Add((item.Kind, item.Mode, item.Text));
        Items.Remove(item);
    }

    /// <summary>Running a dropped expression is how it comes back: the row was hidden, not forgotten.</summary>
    public void Unhide((TargetKind Kind, PredicateMode Mode, string Text) key) => _hidden.Remove(key);
}
