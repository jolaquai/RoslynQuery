using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;

using IServiceProvider = System.IServiceProvider;
using TextSpan = Microsoft.CodeAnalysis.Text.TextSpan;

namespace RoslynQuery.Query;

/// <summary>A document to scan, optionally narrowed to one declaration's span.</summary>
internal readonly struct ScopeUnit(Document document, TextSpan? restriction)
{
    public readonly Document Document = document;
    public readonly TextSpan? Restriction = restriction;
}

internal sealed class ActiveContext
{
    public string FilePath { get; set; }
    public int Line { get; set; }
    public int Column { get; set; }
}

internal static class ScopeResolver
{
    /// <summary>Reads the last active code view. Main thread only.</summary>
    public static ActiveContext GetActiveContext(IServiceProvider serviceProvider)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (serviceProvider.GetService(typeof(SVsTextManager)) is not IVsTextManager manager)
            return null;
        if (ErrorHandler.Failed(manager.GetActiveView(0, null, out var view)) || view is null) return null;
        if (ErrorHandler.Failed(view.GetCaretPos(out var line, out var column))) return null;
        if (ErrorHandler.Failed(view.GetBuffer(out var buffer)) || buffer is null) return null;

        if (buffer is not IPersistFileFormat persist) return null;
        if (ErrorHandler.Failed(persist.GetCurFile(out var path, out _)) || string.IsNullOrEmpty(path)) return null;

        return new ActiveContext { FilePath = path, Line = line, Column = column };
    }

    public static async Task<IReadOnlyList<ScopeUnit>> ResolveAsync(
        Solution solution, ScopeKind scope, ActiveContext active, bool includeGenerated, CancellationToken cancellationToken)
    {
        var document = FindDocument(solution, active?.FilePath);

        // The predicate is compiled against the C# SyntaxKind, so a VB active document is no scope
        // at all rather than a document whose every node silently reports SyntaxKind.None.
        if (document != null && document.Project.Language != LanguageNames.CSharp) document = null;

        switch (scope)
        {
            case ScopeKind.Solution:
                return await CollectAsync(solution.Projects, includeGenerated, cancellationToken).ConfigureAwait(false);

            case ScopeKind.Project:
                if (document is null) return Array.Empty<ScopeUnit>();
                return await CollectAsync([document.Project], includeGenerated, cancellationToken).ConfigureAwait(false);

            case ScopeKind.Document:
                if (document is null) return Array.Empty<ScopeUnit>();
                return [new ScopeUnit(document, null)];

            case ScopeKind.ContainingMember:
            case ScopeKind.ContainingType:
                if (document is null) return Array.Empty<ScopeUnit>();
                var unit = await ResolveDeclarationAsync(document, scope, active, cancellationToken).ConfigureAwait(false);
                return unit.HasValue ? [unit.Value] : Array.Empty<ScopeUnit>();

            default:
                throw new ArgumentOutOfRangeException(nameof(scope));
        }
    }

    private static async Task<IReadOnlyList<ScopeUnit>> CollectAsync(
        IEnumerable<Project> projects, bool includeGenerated, CancellationToken cancellationToken)
    {
        var units = new List<ScopeUnit>();

        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The predicate template is C#-typed (SyntaxKind, CSharpSyntaxNode), so VB documents
            // could never satisfy it and are skipped rather than counted as errors.
            if (project.Language != LanguageNames.CSharp) continue;

            foreach (var document in project.Documents)
            {
                if (!document.SupportsSyntaxTree) continue;
                if (!includeGenerated && IsGenerated(document)) continue;
                units.Add(new ScopeUnit(document, null));
            }

            if (!includeGenerated) continue;

            foreach (var generated in await project.GetSourceGeneratedDocumentsAsync(cancellationToken).ConfigureAwait(false))
                units.Add(new ScopeUnit(generated, null));
        }

        return units;
    }

    private static async Task<ScopeUnit?> ResolveDeclarationAsync(
        Document document, ScopeKind scope, ActiveContext active, CancellationToken cancellationToken)
    {
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var position = ToPosition(text, active);
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (model is null) return null;

        var symbol = model.GetEnclosingSymbol(position, cancellationToken);
        while (symbol != null && !IsDeclarationSymbol(symbol)) symbol = symbol.ContainingSymbol;
        if (symbol is null) return null;

        if (scope == ScopeKind.ContainingType) symbol = symbol as INamedTypeSymbol ?? symbol.ContainingType;
        if (symbol is null) return null;

        var reference = symbol.DeclaringSyntaxReferences.FirstOrDefault(r => r.SyntaxTree.FilePath == document.FilePath && r.Span.Contains(position))
            ?? symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (reference is null) return null;

        var owner = document.Project.Solution.GetDocument(reference.SyntaxTree) ?? document;
        return new ScopeUnit(owner, reference.Span);
    }

    private static bool IsDeclarationSymbol(ISymbol symbol)
    {
        if (symbol is IMethodSymbol method && method.MethodKind == MethodKind.AnonymousFunction) return false;

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

    private static bool IsGenerated(Document document)
    {
        var name = document.Name ?? string.Empty;
        return name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase);
    }
}
