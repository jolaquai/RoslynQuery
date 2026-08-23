using System.IO;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace RoslynQuery.Tests;

/// <summary>
/// Shared <see cref="AdhocWorkspace"/> fixture for the reference-graph tests. Documents get real
/// file paths because <c>Solution.GetDocumentIdsWithFilePath</c> is how the production code finds
/// the caret's document.
/// </summary>
internal static class TestSolutions
{
    public const string RootPath = @"C:\RoslynQueryTests";

    public static string PathFor(string name) => Path.Combine(RootPath, name);

    public static Solution Create(params (string Name, string Source)[] documents) =>
        Create("TestProject", documents);

    public static Solution Create(string projectName, params (string Name, string Source)[] documents)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();

        var projectInfo = ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            projectName,
            projectName,
            LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);

        var solution = workspace.AddProject(projectInfo).Solution;

        foreach (var (name, source) in documents)
        {
            solution = solution.AddDocument(
                DocumentId.CreateNewId(projectId), name, SourceText.From(source), filePath: PathFor(name));
        }

        return solution;
    }

    /// <summary>
    /// Several projects over one physical file, which is what a multi-targeted project (or a linked
    /// file) looks like to Roslyn: one <c>DocumentId</c> per project, one path on disk.
    /// </summary>
    public static Solution MultiTargeted(int projectCount, string name, string source)
    {
        var solution = new AdhocWorkspace().CurrentSolution;

        for (var i = 0; i < projectCount; i++)
        {
            var projectId = ProjectId.CreateNewId();

            solution = solution.AddProject(ProjectInfo.Create(
                projectId, VersionStamp.Create(), $"P{i}", $"P{i}", LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]));

            solution = solution.AddDocument(
                DocumentId.CreateNewId(projectId), name, SourceText.From(source), filePath: PathFor(name));
        }

        return solution;
    }

    public static Document Document(Solution solution, string name) =>
        solution.Projects.SelectMany(p => p.Documents).First(d => d.Name == name);

    /// <summary>Strips the `$$` caret marker out of a source string and reports where it was.</summary>
    public static (string Source, int Line, int Column) ExtractCaret(string source)
    {
        var index = source.IndexOf("$$");
        var stripped = source.Remove(index, 2);
        var text = SourceText.From(stripped);
        var position = text.Lines.GetLinePosition(index);

        return (stripped, position.Line, position.Character);
    }
}
