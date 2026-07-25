using System;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;

using RoslynQuery.Query;

namespace RoslynQuery.Editor;

/// <summary>
/// A throwaway workspace whose single document holds the wrapped predicate. Completion and quick
/// info run against a detached solution snapshot, so nothing here is ever mutated per keystroke.
/// </summary>
internal static class PredicateDocumentFactory
{
    private static readonly object Gate = new object();
    private static AdhocWorkspace _workspace;
    private static DocumentId _documentId;
    private static bool _failed;

    public static Document Create(string source)
    {
        lock (Gate)
        {
            if (_failed) return null;
            if (_workspace is null && !TryInitialize()) return null;
        }

        return _workspace.CurrentSolution.WithDocumentText(_documentId, SourceText.From(source)).GetDocument(_documentId);
    }

    private static bool TryInitialize()
    {
        try
        {
            AdhocWorkspace workspace;
            try { workspace = new AdhocWorkspace(MefHostServices.Create(MefHostServices.DefaultAssemblies)); }
            catch (Exception) { workspace = new AdhocWorkspace(); }

            var projectId = ProjectId.CreateNewId("RoslynQueryPredicate");
            var project = workspace.AddProject(ProjectInfo
                .Create(projectId, VersionStamp.Create(), "RoslynQueryPredicate", "RoslynQueryPredicate", LanguageNames.CSharp)
                .WithMetadataReferences(PredicateCompiler.References)
                .WithParseOptions(PredicateTemplate.ParseOptions)
                .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)));

            var document = workspace.AddDocument(project.Id, "Predicate.cs", SourceText.From(string.Empty));

            _workspace = workspace;
            _documentId = document.Id;
            return true;
        }
        catch (Exception)
        {
            // No IntelliSense is a degraded experience, not a broken tool window.
            _failed = true;
            return false;
        }
    }
}
