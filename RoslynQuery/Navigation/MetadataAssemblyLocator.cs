using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;

using RoslynQuery.ReferenceGraph;

namespace RoslynQuery.Navigation;

/// <summary>The file a metadata symbol's assembly comes from, exactly as the project that reached it references it.</summary>
internal static class MetadataAssemblyLocator
{
    public static async Task<string> PathOfAsync(SymbolIdentity identity, Solution solution, CancellationToken cancellationToken)
    {
        if (identity.IsEmpty) return null;

        var project = solution?.GetProject(identity.ProjectId);
        if (project is null) return null;

        var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        var symbol = compilation is null ? null : DocumentationCommentId.GetFirstSymbolForDeclarationId(identity.DeclarationId, compilation);
        if (symbol?.ContainingAssembly is null) return null;

        return (compilation.GetMetadataReference(symbol.ContainingAssembly) as PortableExecutableReference)?.FilePath;
    }
}
