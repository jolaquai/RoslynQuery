using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

using RoslynQuery.Query;

namespace RoslynQuery.ReferenceGraph;

/// <summary>Turns a caret position into the symbol a reference graph can be rooted at.</summary>
internal static class SymbolResolver
{
    private const int MaxAncestorProbes = 4;

    public static async Task<ISymbol> ResolveAtCaretAsync(Solution solution, ActiveContext active, CancellationToken cancellationToken)
    {
        var document = FindDocument(solution, active?.FilePath);
        if (document is null || document.Project.Language != LanguageNames.CSharp) return null;

        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (model is null || root is null) return null;

        var token = FindToken(root, ToPosition(text, active));

        var node = token.Parent;

        for (var probe = 0; node != null && probe < MaxAncestorProbes; probe++, node = node.Parent)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var declared = model.GetDeclaredSymbol(node, cancellationToken);
            if (declared != null) return Accept(declared);

            var info = model.GetSymbolInfo(node, cancellationToken);
            var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();

            // Anything that binds at all is the user's answer, even when it is a kind we reject: a
            // caret sitting on a local must not silently walk out to the enclosing method.
            if (symbol != null) return Accept(symbol);
        }

        return null;
    }

    /// <summary>What a caret may root a graph at. Wider than <see cref="IsGraphTarget"/>: namespaces and position-identified symbols can be analysed but are not rows.</summary>
    public static bool IsSupportedRoot(ISymbol symbol) =>
        IsGraphTarget(symbol)
        || symbol is INamespaceSymbol { IsGlobalNamespace: false }
        || (SymbolIdentity.IdentifiedByPosition(symbol) && !symbol.DeclaringSyntaxReferences.IsEmpty);

    /// <summary>What becomes a row in a branch: a member, a type, or a local function.</summary>
    public static bool IsGraphTarget(ISymbol symbol) =>
        IsAttributionTarget(symbol) || symbol is IMethodSymbol { MethodKind: MethodKind.LocalFunction };

    /// <summary>Where an incoming occurrence is attributed: never a local function or lambda, which are stepped over to the member holding them.</summary>
    public static bool IsAttributionTarget(ISymbol symbol)
    {
        if (symbol is null) return false;

        if (symbol is IMethodSymbol method
            && (method.MethodKind == MethodKind.AnonymousFunction || method.MethodKind == MethodKind.LocalFunction))
            return false;

        if (symbol is INamedTypeSymbol type && type.TypeKind == TypeKind.Error) return false;

        switch (symbol.Kind)
        {
            case SymbolKind.Method:
            case SymbolKind.Property:
            case SymbolKind.Field:
            case SymbolKind.Event:
            case SymbolKind.NamedType:
                return true;
            default:
                return false;
        }
    }

    private static ISymbol Accept(ISymbol symbol) => IsSupportedRoot(symbol) ? symbol : null;

    /// <summary>A caret parked immediately after an identifier still counts as being on it.</summary>
    private static SyntaxToken FindToken(SyntaxNode root, int position)
    {
        var token = root.FindToken(position, findInsideTrivia: true);
        if (token.IsKind(SyntaxKind.IdentifierToken) || position <= 0) return token;

        var previous = root.FindToken(position - 1, findInsideTrivia: true);
        return previous.IsKind(SyntaxKind.IdentifierToken) && previous.Span.End == position ? previous : token;
    }

    private static Document FindDocument(Solution solution, string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return null;

        var id = solution.GetDocumentIdsWithFilePath(filePath).FirstOrDefault();
        return id is null ? null : solution.GetDocument(id);
    }

    private static int ToPosition(SourceText text, ActiveContext active)
    {
        if (active is null || active.Line < 0 || active.Line >= text.Lines.Count) return 0;

        var line = text.Lines[active.Line];
        return Math.Min(line.Start + Math.Max(0, active.Column), line.End);
    }
}
