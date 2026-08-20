using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace RoslynQuery.ReferenceGraph;

/// <summary>
/// Builds one level of the reference graph at a time. Every method here returns detached
/// <see cref="ReferenceGraphNode"/>s; nothing is cached between calls, because the tool window
/// re-expands from scratch whenever the filter or the scope changes.
/// </summary>
internal static class ReferenceGraphEngine
{
    /// <summary>Past this the tree stops being navigable, so the rest collapses into one row.</summary>
    public const int MaxNodes = 200;

    private static readonly SymbolDisplayFormat NodeFormat = new SymbolDisplayFormat(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters
            | SymbolDisplayMemberOptions.IncludeContainingType
            | SymbolDisplayMemberOptions.IncludeExplicitInterface,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    public static async Task<IReadOnlyList<ReferenceGraphNode>> FindIncomingAsync(
        ISymbol target,
        Solution solution,
        IImmutableSet<Document> documents,
        ReferenceUsageKind filter,
        ReferenceGraphNode parent,
        CancellationToken cancellationToken)
    {
        if (target is null || solution is null) return [];

        var references = await SymbolFinder.FindReferencesAsync(target, solution, documents, cancellationToken).ConfigureAwait(false);
        var groups = new GroupSet();
        var models = new Dictionary<DocumentId, SemanticModel>();
        var roots = new Dictionary<SyntaxTree, SyntaxNode>();

        foreach (var reference in references)
        {
            foreach (var location in reference.Locations)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Candidate locations are the ones Roslyn could not bind with confidence; including
                // them turns every failed overload resolution into a phantom caller.
                if (location.IsCandidateLocation) continue;

                var document = location.Document;
                var tree = location.Location.SourceTree;
                if (document is null || tree is null) continue;

                if (!roots.TryGetValue(tree, out var root))
                {
                    root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);
                    roots[tree] = root;
                }

                var span = location.Location.SourceSpan;
                var occurrence = root.FindNode(span, getInnermostNodeForTie: true);

                var kind = ReferenceUsageClassifier.Classify(occurrence, reference.Definition);
                if ((kind & filter) == ReferenceUsageKind.None) continue;

                if (!models.TryGetValue(document.Id, out var model))
                {
                    model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                    models[document.Id] = model;
                }

                if (model is null) continue;

                var enclosing = EnclosingDeclaration(model, occurrence, span.Start, cancellationToken);
                if (enclosing is null) continue;

                groups.Add(enclosing, solution, document.Project.Id, new ReferenceLocationInfo(document.Id, span, kind));
            }
        }

        return groups.Build(ReferenceDirection.Incoming, parent);
    }

    /// <summary>
    /// The declaration an occurrence belongs to. Walks syntax rather than calling
    /// <c>GetEnclosingSymbol</c> first, because the binder's answer for anything outside a body -
    /// a parameter's type, a return type, an attribute - is the containing type, not the member the
    /// user is looking at. Lambdas and local functions are stepped over on the way up, since neither
    /// is a declaration the graph can show a row for.
    /// </summary>
    private static ISymbol EnclosingDeclaration(
        SemanticModel model, SyntaxNode occurrence, int position, CancellationToken cancellationToken)
    {
        for (var node = occurrence; node != null; node = node.Parent)
        {
            // A field's symbol hangs off the declarator, not off the FieldDeclaration above it.
            if (!(node is MemberDeclarationSyntax || node is AccessorDeclarationSyntax || node is VariableDeclaratorSyntax))
                continue;

            var declared = model.GetDeclaredSymbol(node, cancellationToken);
            if (declared != null) return Normalize(declared);
        }

        return Normalize(model.GetEnclosingSymbol(position, cancellationToken));
    }

    private static ISymbol Normalize(ISymbol symbol)
    {
        while (symbol != null && !SymbolResolver.IsSupportedRoot(symbol)) symbol = symbol.ContainingSymbol;

        // An accessor is shown as the property or event it belongs to, the way Call Hierarchy does.
        if (symbol is IMethodSymbol method && method.AssociatedSymbol != null) return method.AssociatedSymbol;

        return symbol;
    }

    private static string Display(ISymbol symbol) => symbol.ToDisplayString(NodeFormat);

    /// <summary>Accumulates locations per symbol while preserving first-seen order.</summary>
    private sealed class GroupSet
    {
        private readonly Dictionary<SymbolIdentity, Group> _byIdentity = [];
        private readonly List<Group> _ordered = [];

        public void Add(ISymbol symbol, Solution solution, ProjectId fallbackProjectId, ReferenceLocationInfo location)
        {
            var identity = SymbolIdentity.Create(symbol, solution, fallbackProjectId);
            if (identity.IsEmpty) return;

            if (!_byIdentity.TryGetValue(identity, out var group))
            {
                group = new Group { Identity = identity, Symbol = symbol };
                _byIdentity[identity] = group;
                _ordered.Add(group);
            }

            group.Locations.Add(location);
        }

        public IReadOnlyList<ReferenceGraphNode> Build(ReferenceDirection direction, ReferenceGraphNode parent)
        {
            var nodes = new List<ReferenceGraphNode>(_ordered.Count);
            var count = 0;

            foreach (var group in _ordered)
            {
                if (count == MaxNodes)
                {
                    nodes.Add(ReferenceGraphNode.CreateMessage($"{_ordered.Count - MaxNodes} more...", parent));
                    break;
                }

                var recursive = parent != null && parent.HasAncestor(group.Identity);

                nodes.Add(new ReferenceGraphNode(
                    Display(group.Symbol),
                    group.Identity,
                    SymbolGlyphs.For(group.Symbol),
                    direction,
                    group.Locations,
                    parent,
                    // A node whose symbol already sits above it would expand into the same subtree
                    // forever, so it is a leaf that says so instead.
                    expandable: !recursive)
                { IsRecursive = recursive });

                count++;
            }

            return nodes;
        }

        private sealed class Group
        {
            public SymbolIdentity Identity;
            public ISymbol Symbol;
            public List<ReferenceLocationInfo> Locations = [];
        }
    }
}
