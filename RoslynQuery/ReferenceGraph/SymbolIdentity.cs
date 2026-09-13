using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoslynQuery.ReferenceGraph;

/// <summary>A symbol reference that survives a compilation snapshot. Not <c>SymbolKey</c>, which is internal to Microsoft.CodeAnalysis.Workspaces.</summary>
internal readonly struct SymbolIdentity : IEquatable<SymbolIdentity>
{
    private const string PositionalPrefix = "@:";

    public SymbolIdentity(ProjectId projectId, string declarationId, bool fromMetadata = false)
    {
        ProjectId = projectId;
        DeclarationId = declarationId;
        IsFromMetadata = fromMetadata;
        FilePath = null;
        Span = default;
        Kind = default;
        Name = null;
    }

    private SymbolIdentity(ProjectId projectId, string filePath, TextSpan span, SymbolKind kind, string name)
    {
        ProjectId = projectId;
        FilePath = filePath;
        Span = span;
        Kind = kind;
        Name = name;
        IsFromMetadata = false;
        DeclarationId = PositionalPrefix + filePath.ToUpperInvariant() + "|" + span.Start + "|" + span.Length + "|" + kind + "|" + name;
    }

    /// <summary>
    /// The project whose compilation resolves this symbol. For a source symbol that is the project
    /// declaring it; for a metadata symbol it is the project through which the symbol was reached, whose
    /// references include the assembly it lives in.
    /// </summary>
    public ProjectId ProjectId { get; }

    /// <summary>A documentation comment id, or for a position-identified symbol an opaque key no documentation id can equal.</summary>
    public string DeclarationId { get; }

    /// <summary>The symbol lives in a referenced assembly, so it has no syntax to walk.</summary>
    public bool IsFromMetadata { get; }

    /// <summary>Set only for a position-identified symbol.</summary>
    public string FilePath { get; }

    /// <summary>Set only for a position-identified symbol: the span of its declaration.</summary>
    public TextSpan Span { get; }

    public SymbolKind Kind { get; }
    public string Name { get; }

    public bool IsEmpty => DeclarationId is null;

    public bool IsPositional => FilePath != null;

    public static SymbolIdentity Create(ISymbol symbol, Solution solution, ProjectId fallbackProjectId)
    {
        if (symbol is null) return default;

        var definition = symbol.OriginalDefinition ?? symbol;
        if (IdentifiedByPosition(definition)) return CreatePositional(definition, solution);

        var declarationId = DocumentationCommentId.CreateDeclarationId(definition);
        if (declarationId is null) return default;

        // A metadata symbol has no declaring project, so the fallback is the only thing that can resolve
        // it - and it works precisely because that project is the one that references the assembly.
        return new SymbolIdentity(
            DeclaringProject(definition, solution) ?? fallbackProjectId, declarationId, IsMetadataSymbol(definition));
    }

    /// <summary>
    /// The symbols a documentation id cannot identify: it is null for locals, parameters and type parameters, and
    /// for local functions and lambdas it resolves to nothing and collides between siblings.
    /// </summary>
    public static bool IdentifiedByPosition(ISymbol symbol)
    {
        switch (symbol)
        {
            case ILocalSymbol _:
            case IParameterSymbol _:
            case ITypeParameterSymbol _:
                return true;
            case IMethodSymbol method:
                return method.MethodKind == MethodKind.LocalFunction || method.MethodKind == MethodKind.AnonymousFunction;
            default:
                return false;
        }
    }

    public static bool IsMetadataSymbol(ISymbol symbol) =>
        symbol != null && symbol.Locations.Length > 0 && symbol.Locations.All(l => l.IsInMetadata);

    public async Task<ISymbol> ResolveAsync(Solution solution, CancellationToken cancellationToken)
    {
        if (IsEmpty) return null;
        if (IsPositional) return await ResolvePositionAsync(solution, cancellationToken).ConfigureAwait(false);

        var project = solution?.GetProject(ProjectId);
        if (project is null) return null;

        var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        return compilation is null ? null : DocumentationCommentId.GetFirstSymbolForDeclarationId(DeclarationId, compilation);
    }

    private static SymbolIdentity CreatePositional(ISymbol symbol, Solution solution)
    {
        var reference = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        var document = reference is null ? null : solution?.GetDocument(reference.SyntaxTree);
        if (document is null || string.IsNullOrEmpty(document.FilePath)) return default;

        return new SymbolIdentity(document.Project.Id, document.FilePath, reference.Span, symbol.Kind, symbol.Name);
    }

    /// <summary>
    /// The exact declaration span or nothing. A node cannot keep the snapshot its span was taken from, so after an
    /// edit above the declaration the row goes stale instead of resolving to a neighbouring declaration.
    /// </summary>
    private async Task<ISymbol> ResolvePositionAsync(Solution solution, CancellationToken cancellationToken)
    {
        if (solution is null) return null;

        var projectId = ProjectId;
        var span = Span;
        var kind = Kind;
        var name = Name;

        var candidates = solution.GetDocumentIdsWithFilePath(FilePath);
        var documentId = candidates.FirstOrDefault(id => id.ProjectId == projectId) ?? candidates.FirstOrDefault();
        var document = documentId is null ? null : solution.GetDocument(documentId);
        if (document is null) return null;

        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || span.End > root.FullSpan.End) return null;

        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (model is null) return null;

        var node = root.FindNode(span, getInnermostNodeForTie: true);
        var symbol = model.GetDeclaredSymbol(node, cancellationToken) ?? model.GetSymbolInfo(node, cancellationToken).Symbol;

        return symbol != null && symbol.Kind == kind && symbol.Name == name && symbol.DeclaringSyntaxReferences.Any(r => r.Span == span)
            ? symbol
            : null;
    }

    private static ProjectId DeclaringProject(ISymbol symbol, Solution solution)
    {
        if (solution is null) return null;

        var tree = symbol.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree;
        return tree is null ? null : solution.GetDocument(tree)?.Project.Id;
    }

    /// <summary>Compares on the declaration id alone - including <see cref="ProjectId"/> made a multi-targeted project show one row per target framework.</summary>
    public bool Equals(SymbolIdentity other) => DeclarationId == other.DeclarationId;

    public override bool Equals(object obj) => obj is SymbolIdentity other && Equals(other);

    public override int GetHashCode() => DeclarationId?.GetHashCode() ?? 0;

    public override string ToString() => DeclarationId ?? "<none>";
}
