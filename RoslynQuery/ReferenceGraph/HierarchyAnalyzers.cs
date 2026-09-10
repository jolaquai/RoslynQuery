using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace RoslynQuery.ReferenceGraph;

/// <summary>
/// The analyzer branches that follow the type system rather than call sites. Unlike the reference
/// analyzers, these reach into referenced assemblies: <c>FindOverridesAsync</c> on
/// <c>Stream.Read</c> answers with <c>MemoryStream.Read</c> and friends.
/// </summary>
internal static class HierarchyAnalyzers
{
    /// <summary>Every step of the override chain, nearest first.</summary>
    public static Task<IReadOnlyList<ReferenceGraphNode>> FindOverridesAsync(
        ISymbol symbol, Solution solution, ReferenceGraphNode parent, CancellationToken cancellationToken)
    {
        var chain = new List<ISymbol>();
        for (var current = Overridden(symbol); current != null; current = Overridden(current)) chain.Add(current);

        return ToNodesAsync(chain, solution, parent, sorted: false, cancellationToken);
    }

    public static async Task<IReadOnlyList<ReferenceGraphNode>> FindOverriddenByAsync(
        ISymbol symbol, Solution solution, IImmutableSet<Project> projects, ReferenceGraphNode parent,
        CancellationToken cancellationToken)
    {
        var overrides = await SymbolFinder
            .FindOverridesAsync(symbol, solution, projects, cancellationToken).ConfigureAwait(false);

        return await ToNodesAsync(overrides, solution, parent, sorted: true, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyList<ReferenceGraphNode>> FindImplementsAsync(
        ISymbol symbol, Solution solution, IImmutableSet<Project> projects, ReferenceGraphNode parent,
        CancellationToken cancellationToken)
    {
        // FindImplementedInterfaceMembersAsync answers empty for an override - the member that actually
        // satisfies the interface is the root of the override chain, not the symbol as given.
        var implemented = await SymbolFinder
            .FindImplementedInterfaceMembersAsync(OverrideRoot(symbol), solution, projects, cancellationToken)
            .ConfigureAwait(false);

        return await ToNodesAsync(implemented, solution, parent, sorted: true, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyList<ReferenceGraphNode>> FindImplementedByAsync(
        ISymbol symbol, Solution solution, IImmutableSet<Project> projects, ReferenceGraphNode parent,
        CancellationToken cancellationToken)
    {
        IEnumerable<ISymbol> results;

        if (symbol is INamedTypeSymbol type && type.TypeKind == TypeKind.Interface)
        {
            results = await SymbolFinder
                .FindImplementationsAsync(type, solution, transitive: true, projects, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            results = await SymbolFinder
                .FindImplementationsAsync(symbol, solution, projects, cancellationToken).ConfigureAwait(false);
        }

        return await ToNodesAsync(results, solution, parent, sorted: true, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyList<ReferenceGraphNode>> FindDerivedTypesAsync(
        ISymbol symbol, Solution solution, IImmutableSet<Project> projects, ReferenceGraphNode parent,
        CancellationToken cancellationToken)
    {
        if (!(symbol is INamedTypeSymbol type)) return [];

        IEnumerable<ISymbol> results = type.TypeKind == TypeKind.Interface
            ? await SymbolFinder
                .FindDerivedInterfacesAsync(type, solution, transitive: true, projects, cancellationToken)
                .ConfigureAwait(false)
            : await SymbolFinder
                .FindDerivedClassesAsync(type, solution, transitive: true, projects, cancellationToken)
                .ConfigureAwait(false);

        return await ToNodesAsync(results, solution, parent, sorted: true, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<ReferenceGraphNode>> ToNodesAsync(
        IEnumerable<ISymbol> symbols, Solution solution, ReferenceGraphNode parent, bool sorted,
        CancellationToken cancellationToken)
    {
        var ordered = sorted ? Order(symbols) : symbols;
        var nodes = new List<ReferenceGraphNode>();
        var seen = new HashSet<SymbolIdentity>();

        foreach (var symbol in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var identity = SymbolIdentity.Create(symbol, solution, parent?.Identity.ProjectId);
            if (identity.IsEmpty || !seen.Add(identity)) continue;

            var locations = await DeclarationLocationsAsync(symbol, solution, cancellationToken).ConfigureAwait(false);
            var recursive = parent != null && parent.HasAncestor(identity);

            var node = ReferenceGraphNode.CreateSymbol(
                ReferenceGraphDisplay.Of(symbol),
                identity,
                SymbolGlyphs.For(symbol),
                ReferenceAnalyzers.For(symbol),
                locations,
                parent,
                analyzable: !recursive);

            node.IsRecursive = recursive;
            nodes.Add(node);
        }

        return nodes;
    }

    /// <summary>A hierarchy row points at the declaration itself, which is the only place it exists.</summary>
    private static async Task<IReadOnlyList<ReferenceLocationInfo>> DeclarationLocationsAsync(
        ISymbol symbol, Solution solution, CancellationToken cancellationToken)
    {
        var reference = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (reference is null) return [];

        var document = solution.GetDocument(reference.SyntaxTree);
        if (document is null) return [];

        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);

        return [ReferenceLocationInfo.Create(document, text, reference.Span, ReferenceUsageKind.None)];
    }

    private static IEnumerable<ISymbol> Order(IEnumerable<ISymbol> symbols) =>
        symbols
            .OrderBy(ReferenceGraphDisplay.Of, StringComparer.Ordinal)
            // Two rows can share a display string across namespaces, and the tie has to break the same
            // way on every refresh.
            .ThenBy(s => DocumentationCommentId.CreateDeclarationId(s.OriginalDefinition ?? s) ?? string.Empty, StringComparer.Ordinal);

    private static ISymbol OverrideRoot(ISymbol symbol)
    {
        for (var current = symbol; current != null;)
        {
            var overridden = Overridden(current);
            if (overridden is null) return current;

            current = overridden;
        }

        return symbol;
    }

    private static ISymbol Overridden(ISymbol symbol)
    {
        switch (symbol)
        {
            case IMethodSymbol method: return method.OverriddenMethod;
            case IPropertySymbol property: return property.OverriddenProperty;
            case IEventSymbol @event: return @event.OverriddenEvent;
            default: return null;
        }
    }
}
