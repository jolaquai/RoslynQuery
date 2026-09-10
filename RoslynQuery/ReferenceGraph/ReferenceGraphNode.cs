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
        ReferenceUsageKind.TypeReference,
        ReferenceUsageKind.Documentation
    ];

    private string _displayText;
    private string _secondaryText;
    private bool _isRecursive;
    private bool _isExpanded;
    private bool _isLoading;

    private ReferenceGraphNode(
        string displayText,
        SymbolIdentity identity,
        SymbolGlyph glyph,
        NodeRole role,
        ReferenceAnalyzerKind? analyzer = null,
        IReadOnlyList<ReferenceLocationInfo> locations = null,
        ReferenceGraphNode parent = null)
    {
        _displayText = displayText;
        Identity = identity;
        Glyph = glyph;
        Role = role;
        Analyzer = analyzer;
        Locations = locations ?? [];
        Parent = parent;
        _secondaryText = Describe(Locations);
    }

    public const string SearchingText = "Searching...";

    public SymbolIdentity Identity { get; }
    public SymbolGlyph Glyph { get; }
    public NodeRole Role { get; }

    /// <summary>Set on analyzer rows only: the branch this row fetches when opened.</summary>
    public ReferenceAnalyzerKind? Analyzer { get; }

    public IReadOnlyList<ReferenceLocationInfo> Locations { get; }
    public ReferenceGraphNode Parent { get; }

    /// <summary>Only an analyzer row runs a fetch; a symbol row builds its branches at construction.</summary>
    public bool IsExpandable => Role == NodeRole.Analyzer;

    public bool IsMessage => Role == NodeRole.Message;

    /// <summary>The row's symbol lives in a referenced assembly, so it has no source to navigate to.</summary>
    public bool IsFromMetadata => Role == NodeRole.Symbol && Identity.IsFromMetadata;

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

    /// <summary>
    /// A symbol row. Its branches are decided here, while the live <see cref="ISymbol"/> is still in
    /// hand, so that opening the row later needs no compilation.
    /// </summary>
    public static ReferenceGraphNode CreateSymbol(
        string displayText,
        SymbolIdentity identity,
        SymbolGlyph glyph,
        IReadOnlyList<ReferenceAnalyzerKind> analyzers,
        IReadOnlyList<ReferenceLocationInfo> locations = null,
        ReferenceGraphNode parent = null,
        bool analyzable = true)
    {
        var node = new ReferenceGraphNode(displayText, identity, glyph, NodeRole.Symbol, locations: locations, parent: parent)
        { IsLoaded = true };

        if (node.Locations.Count > 1) node.Children.Add(node.BuildLocationsBranch());

        if (analyzable && analyzers != null)
            foreach (var kind in analyzers)
                node.Children.Add(CreateAnalyzer(kind, node));

        return node;
    }

    /// <summary>A branch row. Carries its parent symbol's identity, which is what the fetch resolves.</summary>
    public static ReferenceGraphNode CreateAnalyzer(ReferenceAnalyzerKind kind, ReferenceGraphNode parent)
    {
        var node = new ReferenceGraphNode(
            kind.Header(), parent?.Identity ?? default, SymbolGlyphs.ForAnalyzer(kind), NodeRole.Analyzer, kind, parent: parent);

        node.Children.Add(CreateMessage(SearchingText, node));

        return node;
    }

    public static ReferenceGraphNode CreateRoot(
        string displayText, SymbolIdentity identity, SymbolGlyph glyph, IReadOnlyList<ReferenceAnalyzerKind> analyzers)
    {
        var root = CreateSymbol(displayText, identity, glyph, analyzers);
        root.IsExpanded = true;

        return root;
    }

    public static ReferenceGraphNode CreateMessage(string text, ReferenceGraphNode parent = null) =>
        new ReferenceGraphNode(text, default, SymbolGlyph.Unknown, NodeRole.Message, parent: parent) { IsLoaded = true };

    /// <summary>A single occurrence: a leaf that exists to be double-clicked.</summary>
    public static ReferenceGraphNode CreateLocation(ReferenceLocationInfo location, ReferenceGraphNode parent) =>
        new ReferenceGraphNode(location.Display, default, SymbolGlyph.Location, NodeRole.Location, locations: [location], parent: parent)
        { IsLoaded = true };

    private ReferenceGraphNode BuildLocationsBranch()
    {
        var branch = new ReferenceGraphNode(
            $"Locations ({Locations.Count})", default, SymbolGlyph.Locations, NodeRole.Locations, parent: this)
        { IsLoaded = true };

        foreach (var location in Locations) branch.Children.Add(CreateLocation(location, branch));

        return branch;
    }

    public static IEnumerable<ReferenceGraphNode> ShallowestExpanded(IEnumerable<ReferenceGraphNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.IsExpandable && node.IsExpanded && node.IsLoaded)
            {
                yield return node;
                continue;
            }

            foreach (var descendant in ShallowestExpanded(node.Children)) yield return descendant;
        }
    }

    public static IEnumerable<ReferenceGraphNode> ShallowestLoaded(IEnumerable<ReferenceGraphNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.IsExpandable && node.IsLoaded)
            {
                yield return node;
                continue;
            }

            foreach (var descendant in ShallowestLoaded(node.Children)) yield return descendant;
        }
    }

    public void ResetToUnloaded()
    {
        if (Role != NodeRole.Analyzer) return;

        Children.Clear();
        Children.Add(CreateMessage(SearchingText, this));
        IsLoaded = false;
        IsExpanded = false;
        SecondaryText = null;
    }

    /// <summary>Includes this node itself, since a direct self-reference is also recursion.</summary>
    public bool HasAncestor(SymbolIdentity identity)
    {
        if (identity.IsEmpty) return false;

        for (var node = this; node != null; node = node.Parent)
            if (node.Identity.Equals(identity)) return true;

        return false;
    }

    public void SetChildren(IEnumerable<ReferenceGraphNode> children)
    {
        Children.Clear();
        foreach (var child in children) Children.Add(child);

        IsLoaded = true;
    }

    /// <summary>
    /// Installs a branch's results and writes ILSpy's header suffix, "(5 in 12 ms)". A branch that found
    /// nothing gets no children and no suffix, which is what leaves it bare and without an expander.
    /// </summary>
    public void ApplyResults(AnalyzerResult result)
    {
        SetChildren(result.Rows);
        SecondaryText = result.Rows.Count == 0
            ? null
            : $"({result.Rows.Count} in {result.ElapsedMilliseconds} ms)";
    }

    /// <summary>"3 refs (2 reads, 1 write)", or just "2 invocations" when there is only one kind.</summary>
    public static string Describe(IReadOnlyList<ReferenceLocationInfo> locations)
    {
        if (locations is null || locations.Count == 0) return null;

        var parts = BreakdownOrder
            .Select(kind => (Kind: kind, Count: locations.Count(l => (l.Kind & kind) != ReferenceUsageKind.None)))
            .Where(p => p.Count > 0)
            .ToList();

        // A hierarchy row's location is a declaration, not a usage, so it has no breakdown to report.
        if (parts.Count == 0) return null;
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
            case ReferenceUsageKind.Documentation: return "doc reference";
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
