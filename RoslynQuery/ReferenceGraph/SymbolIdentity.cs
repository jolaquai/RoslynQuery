using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;

namespace RoslynQuery.ReferenceGraph;

/// <summary>A symbol reference that survives a compilation snapshot. Not <c>SymbolKey</c>, which is internal to Microsoft.CodeAnalysis.Workspaces.</summary>
internal readonly struct SymbolIdentity : IEquatable<SymbolIdentity>
{
    public SymbolIdentity(ProjectId projectId, string declarationId)
    {
        ProjectId = projectId;
        DeclarationId = declarationId;
    }

    public ProjectId ProjectId { get; }
    public string DeclarationId { get; }

    public bool IsEmpty => DeclarationId is null;

    public static SymbolIdentity Create(ISymbol symbol, Solution solution, ProjectId fallbackProjectId)
    {
        if (symbol is null) return default;

        var definition = symbol.OriginalDefinition ?? symbol;
        var declarationId = DocumentationCommentId.CreateDeclarationId(definition);
        if (declarationId is null) return default;

        return new SymbolIdentity(DeclaringProject(definition, solution) ?? fallbackProjectId, declarationId);
    }

    public async Task<ISymbol> ResolveAsync(Solution solution, CancellationToken cancellationToken)
    {
        if (IsEmpty) return null;

        var project = solution?.GetProject(ProjectId);
        if (project is null) return null;

        var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        return compilation is null ? null : DocumentationCommentId.GetFirstSymbolForDeclarationId(DeclarationId, compilation);
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
