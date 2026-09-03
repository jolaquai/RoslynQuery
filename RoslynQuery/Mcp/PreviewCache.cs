using System;
using System.Collections.Generic;

using Microsoft.CodeAnalysis;

using RoslynQuery.Mcp.Contracts;
using RoslynQuery.Replace;

namespace RoslynQuery.Mcp;

/// <summary>
/// Holds each generated replace preview against the <see cref="Solution"/> snapshot it ran on, so a
/// later <see cref="IRoslynQueryRpc.ApplyReplaceAsync"/> can redeem it by id. A held Solution roots
/// its whole compilation graph, so entries are bounded both ways: an absolute TTL and a hard count
/// cap, both enforced lazily on every access rather than by a background timer.
/// </summary>
internal sealed class PreviewCache
{
    internal sealed class Entry
    {
        public Solution Solution;
        public IReadOnlyList<ReplacementItem> Items;
        public TargetKind Target;
        public DateTime CreatedUtc;
    }

    private readonly int _maxEntries;
    private readonly TimeSpan _ttl;
    private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>();
    private readonly object _sync = new object();

    public PreviewCache(int maxEntries = 16, TimeSpan? ttl = null)
    {
        _maxEntries = maxEntries;
        _ttl = ttl ?? TimeSpan.FromMinutes(5);
    }

    public string Add(Solution solution, IReadOnlyList<ReplacementItem> items, TargetKind target)
    {
        var id = Guid.NewGuid().ToString("N");
        var entry = new Entry { Solution = solution, Items = items, Target = target, CreatedUtc = DateTime.UtcNow };

        lock (_sync)
        {
            Sweep();
            while (_entries.Count >= _maxEntries) EvictOldest();
            _entries[id] = entry;
        }

        return id;
    }

    public bool TryGet(string id, out Entry entry)
    {
        lock (_sync)
        {
            Sweep();
            return _entries.TryGetValue(id ?? string.Empty, out entry);
        }
    }

    public void Remove(string id)
    {
        if (id is null) return;
        lock (_sync) _entries.Remove(id);
    }

    private void Sweep()
    {
        var cutoff = DateTime.UtcNow - _ttl;
        List<string> stale = null;
        foreach (var pair in _entries)
        {
            if (pair.Value.CreatedUtc < cutoff)
                (stale ??= new List<string>()).Add(pair.Key);
        }
        if (stale is null) return;
        foreach (var key in stale) _entries.Remove(key);
    }

    private void EvictOldest()
    {
        string oldestKey = null;
        var oldest = DateTime.MaxValue;
        foreach (var pair in _entries)
        {
            if (pair.Value.CreatedUtc < oldest)
            {
                oldest = pair.Value.CreatedUtc;
                oldestKey = pair.Key;
            }
        }
        if (oldestKey != null) _entries.Remove(oldestKey);
    }
}
