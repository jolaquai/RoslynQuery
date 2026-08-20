using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

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
    /// What <paramref name="root"/> itself references. Unlike the incoming path this never leaves the
    /// root's own declarations, so there is no scope to honour: a member's outgoing set is whatever is
    /// written inside it, and a type's is whatever is written inside its members and its base list.
    /// </summary>
    public static async Task<IReadOnlyList<ReferenceGraphNode>> FindOutgoingAsync(
        ISymbol root,
        Solution solution,
        ReferenceUsageKind filter,
        ReferenceGraphNode parent,
        CancellationToken cancellationToken)
    {
        if (root is null || solution is null) return [];

        var groups = new GroupSet();

        foreach (var reference in DeclaringReferences(root))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var document = solution.GetDocument(reference.SyntaxTree);
            if (document is null) continue;

            var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            if (model is null) continue;

            var declaration = await reference.GetSyntaxAsync(cancellationToken).ConfigureAwait(false);

            foreach (var scope in Scopes(declaration))
                Walk(scope, model, document, filter, groups, solution, cancellationToken);
        }

        return groups.Build(ReferenceDirection.Outgoing, parent);
    }

    private static void Walk(
        SyntaxNode scope, SemanticModel model, Document document, ReferenceUsageKind filter,
        GroupSet groups, Solution solution, CancellationToken cancellationToken)
    {
        // A nested type is its own row in the graph, so its contents are not part of the outer type's
        // outgoing set. The scope itself always gets descended into, nested or not.
        bool Descend(SyntaxNode node) =>
            ReferenceEquals(node, scope) || !(node is BaseTypeDeclarationSyntax || node is DelegateDeclarationSyntax);

        foreach (var node in scope.DescendantNodesAndSelf(Descend))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCandidate(node)) continue;

            var info = model.GetSymbolInfo(node, cancellationToken);
            var symbol = info.Symbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null);
            if (symbol is null) continue;

            var kind = ReferenceUsageClassifier.Classify(node, symbol);
            if ((kind & filter) == ReferenceUsageKind.None) continue;

            if (!SymbolResolver.IsSupportedRoot(symbol)) continue;

            groups.Add(symbol, solution, document.Project.Id, new ReferenceLocationInfo(document.Id, node.Span, kind));
        }
    }

    /// <summary>
    /// The nodes whose symbol is worth binding. Restricted to name nodes so that `a.B.C()` is counted
    /// once per symbol rather than once per enclosing expression, plus the creation forms, which carry
    /// the constructor the name nodes alone would miss.
    /// </summary>
    private static bool IsCandidate(SyntaxNode node)
    {
        switch (node)
        {
            // `new Foo()` is reported as its constructor by the creation expression below, so the type
            // name itself would only duplicate the same span. `var` binds to whatever it infers, but
            // the user never wrote that type, so it is not a reference they made.
            case IdentifierNameSyntax identifier when identifier.IsVar:
                return false;
            case SimpleNameSyntax name:
                return !(name.Parent is ObjectCreationExpressionSyntax creation && creation.Type == name);
            case ObjectCreationExpressionSyntax _:
            case ImplicitObjectCreationExpressionSyntax _:
            case ConstructorInitializerSyntax _:
                return true;
            default:
                return false;
        }
    }

    /// <summary>A field declares its symbol on the declarator, which does not carry the field's type.</summary>
    private static IEnumerable<SyntaxNode> Scopes(SyntaxNode declaration)
    {
        yield return declaration;

        if (declaration is VariableDeclaratorSyntax declarator && declarator.Parent is VariableDeclarationSyntax variable)
            yield return variable.Type;
    }

    /// <summary>Every syntax that declares the symbol, both halves of a partial included.</summary>
    private static IEnumerable<SyntaxReference> DeclaringReferences(ISymbol symbol)
    {
        var references = symbol.DeclaringSyntaxReferences.ToList();

        if (symbol is IMethodSymbol method)
        {
            if (method.PartialImplementationPart != null) references.AddRange(method.PartialImplementationPart.DeclaringSyntaxReferences);
            if (method.PartialDefinitionPart != null) references.AddRange(method.PartialDefinitionPart.DeclaringSyntaxReferences);
        }

        var seen = new HashSet<(SyntaxTree, TextSpan)>();
        return references.Where(r => seen.Add((r.SyntaxTree, r.Span))).ToList();
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

    /// <summary>Accumulates locations per symbol while preserving first-seen order.</summary>
    private sealed class GroupSet
    {
        private readonly Dictionary<SymbolIdentity, Group> _byIdentity = [];
        private readonly List<Group> _ordered = [];

        public void Add(ISymbol symbol, Solution solution, ProjectId fallbackProjectId, ReferenceLocationInfo location)
        {
            symbol = NormalizeTarget(symbol);

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
                    ReferenceGraphDisplay.Of(group.Symbol),
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

        /// <summary>
        /// Collapses the shapes that share one identity: an extension method called in reduced form,
        /// and a constructed generic, both belong on the row of the thing they were built from.
        /// </summary>
        private static ISymbol NormalizeTarget(ISymbol symbol)
        {
            if (symbol is IMethodSymbol method && method.ReducedFrom != null) symbol = method.ReducedFrom;

            return symbol.OriginalDefinition ?? symbol;
        }

        private sealed class Group
        {
            public SymbolIdentity Identity;
            public ISymbol Symbol;
            public List<ReferenceLocationInfo> Locations = [];
        }
    }
}
