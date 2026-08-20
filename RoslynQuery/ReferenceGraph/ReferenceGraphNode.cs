using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoslynQuery.ReferenceGraph;

/// <summary>
/// One row in the reference graph. Holds no live Roslyn object for the same reason
/// <see cref="Query.QueryHit"/> does not: a node outlives the compilation that produced it, and the
/// tool window can keep hundreds of them alive for as long as it stays open.
/// </summary>
internal sealed class ReferenceGraphNode : INotifyPropertyChanged
{
    private static readonly ReferenceUsageKind[] BreakdownOrder =
    [
        ReferenceUsageKind.Invocation,
        ReferenceUsageKind.Read,
        ReferenceUsageKind.Write,
        ReferenceUsageKind.Construction,
        ReferenceUsageKind.TypeReference
    ];

    private string _displayText;
    private string _secondaryText;
    private bool _isRecursive;
    private bool _isExpanded;
    private bool _isLoading;

    public ReferenceGraphNode(
        string displayText,
        SymbolIdentity identity,
        SymbolGlyph glyph,
        ReferenceDirection direction,
        IReadOnlyList<ReferenceLocationInfo> locations = null,
        ReferenceGraphNode parent = null,
        bool expandable = true)
    {
        _displayText = displayText;
        Identity = identity;
        Glyph = glyph;
        Direction = direction;
        Locations = locations ?? [];
        Parent = parent;
        IsExpandable = expandable;
        _secondaryText = Describe(Locations);

        if (expandable) Children.Add(CreateMessage(SearchingText, this));
    }

    public const string SearchingText = "Searching...";

    public SymbolIdentity Identity { get; }
    public SymbolGlyph Glyph { get; }
    public ReferenceDirection Direction { get; }
    public IReadOnlyList<ReferenceLocationInfo> Locations { get; }
    public ReferenceGraphNode Parent { get; }

    /// <summary>False for the "Searching..." / "N more..." rows, which are text and nothing else.</summary>
    public bool IsExpandable { get; }
    public bool IsMessage { get; private set; }

    /// <summary>Set once the lazy fetch has replaced the seeded placeholder.</summary>
    public bool IsLoaded { get; set; }

    public ObservableCollection<ReferenceGraphNode> Children { get; } = [];

    public DocumentId DocumentId => Locations.Count == 0 ? null : Locations[0].DocumentId;
    public TextSpan Span => Locations.Count == 0 ? default : Locations[0].Span;

    public string DisplayText
    {
        get => _displayText;
        set => Set(ref _displayText, value);
    }

    public string SecondaryText
    {
        get => _secondaryText;
        set => Set(ref _secondaryText, value);
    }

    /// <summary>The node's symbol already appears above it, so expanding it would loop forever.</summary>
    public bool IsRecursive
    {
        get => _isRecursive;
        set => Set(ref _isRecursive, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => Set(ref _isLoading, value);
    }

    public static ReferenceGraphNode CreateMessage(string text, ReferenceGraphNode parent = null) =>
        new ReferenceGraphNode(text, default, SymbolGlyph.Unknown, parent?.Direction ?? ReferenceDirection.Incoming,
            parent: parent, expandable: false)
        { IsMessage = true };

    /// <summary>
    /// Walks the parent chain, this node included: a node's own symbol turning up again below it is
    /// exactly the self-recursion the graph has to stop at.
    /// </summary>
    public bool HasAncestor(SymbolIdentity identity)
    {
        if (identity.IsEmpty) return false;

        for (var node = this; node != null; node = node.Parent)
            if (node.Identity.Equals(identity)) return true;

        return false;
    }

    /// <summary>Drops the seeded placeholder and takes the fetched rows in one shot.</summary>
    public void SetChildren(IEnumerable<ReferenceGraphNode> children)
    {
        Children.Clear();
        foreach (var child in children) Children.Add(child);
        IsLoaded = true;
    }

    /// <summary>"3 refs (2 reads, 1 write)", or just "2 invocations" when there is only one kind.</summary>
    public static string Describe(IReadOnlyList<ReferenceLocationInfo> locations)
    {
        if (locations is null || locations.Count == 0) return null;

        var parts = BreakdownOrder
            .Select(kind => (Kind: kind, Count: locations.Count(l => (l.Kind & kind) != ReferenceUsageKind.None)))
            .Where(p => p.Count > 0)
            .ToList();

        if (parts.Count == 1) return $"{parts[0].Count} {Pluralize(NameOf(parts[0].Kind), parts[0].Count)}";

        var builder = new StringBuilder();
        builder.Append(locations.Count).Append(locations.Count == 1 ? " ref (" : " refs (");

        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0) builder.Append(", ");
            builder.Append(parts[i].Count).Append(' ').Append(Pluralize(NameOf(parts[i].Kind), parts[i].Count));
        }

        return builder.Append(')').ToString();
    }

    private static string NameOf(ReferenceUsageKind kind)
    {
        switch (kind)
        {
            case ReferenceUsageKind.Invocation: return "invocation";
            case ReferenceUsageKind.Read: return "read";
            case ReferenceUsageKind.Write: return "write";
            case ReferenceUsageKind.Construction: return "construction";
            case ReferenceUsageKind.TypeReference: return "type reference";
            default: return "reference";
        }
    }

    private static string Pluralize(string word, int count) => count == 1 ? word : word + "s";

    public event PropertyChangedEventHandler PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
