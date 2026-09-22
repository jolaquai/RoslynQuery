using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

using RoslynQuery.Query;

using Xunit;

namespace RoslynQuery.Tests;

[Collection(PredicateCompilerCacheCollection.Name)]
public class QueryEngineLinkedFileTests
{
    /// <summary>One file linked into a project per symbol set, the way a multi-targeted project shows up in Roslyn.</summary>
    private static ScopeUnit[] LinkedUnits(string source, params string[][] symbolsPerProject)
    {
        var workspace = new AdhocWorkspace();
        var ids = new List<DocumentId>();

        foreach (var symbols in symbolsPerProject)
        {
            var project = workspace.AddProject(ProjectInfo.Create(
                ProjectId.CreateNewId(),
                VersionStamp.Create(),
                "Linked",
                "Linked",
                LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                parseOptions: CSharpParseOptions.Default.WithPreprocessorSymbols(symbols),
                metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]));

            var document = workspace.AddDocument(DocumentInfo.Create(
                DocumentId.CreateNewId(project.Id),
                "Linked.cs",
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From(source), VersionStamp.Create())),
                filePath: @"C:\src\Linked.cs"));

            ids.Add(document.Id);
        }

        // From one snapshot, as ScopeResolver does: a document taken before its copies were added has no links.
        var solution = workspace.CurrentSolution;
        return [.. ids.Select(id => new ScopeUnit(solution.GetDocument(id), null, filterGenerated: false))];
    }

    // A lone [] would bind as the whole params array, not as one project with no symbols.
    private static ScopeUnit[] OneCopy(string source) => LinkedUnits(source, [[]]);

    private static Task<QueryOutcome> RunAsync(IReadOnlyList<ScopeUnit> units, TargetKind target, string expression, List<QueryHit> hits, int maxResults = 100) =>
        QueryEngine.RunAsync(
            units, target, expression, PredicateCompiler.Compile(target, expression), maxResults,
            onBatch: batch => { lock (hits) hits.AddRange(batch); },
            CancellationToken.None);

    [Fact]
    public async Task RunAsync_LinkedCopiesOfOneFile_ReportEachLocationOnce()
    {
        var units = LinkedUnits("class C { void M() { int a = 1; int b = 2; } }", [], [], [], []);
        var hits = new List<QueryHit>();

        var outcome = await RunAsync(units, TargetKind.SyntaxNode, "n.IsKind(SyntaxKind.LocalDeclarationStatement)", hits);

        Assert.Equal(2, outcome.Matched);
        Assert.Equal(1, outcome.Documents);
        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.Equal(units[0].Document.Id, h.FileId));
    }

    [Fact]
    public async Task RunAsync_LinkedCopies_DoNotSpendTheCap()
    {
        var units = LinkedUnits("class C { void M() { int a = 1; int b = 2; } }", [], [], []);
        var hits = new List<QueryHit>();

        var outcome = await RunAsync(units, TargetKind.SyntaxNode, "n.IsKind(SyntaxKind.LocalDeclarationStatement)", hits, maxResults: 2);

        Assert.Equal(2, outcome.Matched);
        Assert.False(outcome.Truncated);
    }

    [Fact]
    public async Task RunAsync_ABranchOnlyOneCopyCompiles_IsFoundAndSortedIntoPlace()
    {
        const string source = "class C\r\n{\r\n#if B\r\n    void M1() { }\r\n#endif\r\n    void M2() { }\r\n}";
        var units = LinkedUnits(source, [], ["B"]);
        var hits = new List<QueryHit>();

        await RunAsync(units, TargetKind.SyntaxNode, "n.IsKind(SyntaxKind.MethodDeclaration)", hits);

        Assert.Collection(
            hits,
            m1 =>
            {
                Assert.StartsWith("void M1", m1.Preview);
                Assert.Equal(units[1].Document.Id, m1.DocumentId);
            },
            m2 =>
            {
                Assert.StartsWith("void M2", m2.Preview);
                Assert.Equal(units[0].Document.Id, m2.DocumentId);
            });
        Assert.All(hits, h => Assert.Equal(units[0].Document.Id, h.FileId));
    }

    /// <summary>The operation walk pops children last-first, so without sorting these come out reversed.</summary>
    [Fact]
    public async Task RunAsync_OperationHits_ComeInDocumentOrder()
    {
        var units = OneCopy("class C { void M() { int a = 1; int b = 2; int c = 3; } }");
        var hits = new List<QueryHit>();

        await RunAsync(units, TargetKind.Operation, "op is ILiteralOperation", hits);

        Assert.Equal(["1", "2", "3"], hits.Select(h => h.Preview));
    }

    [Fact]
    public async Task RunAsync_CappedPartWayThroughAFile_PublishesWhatItFoundInOrder()
    {
        var units = OneCopy("class C { void M() { int a = 1; int b = 2; int c = 3; } }");
        var hits = new List<QueryHit>();

        var outcome = await RunAsync(units, TargetKind.Operation, "op is ILiteralOperation", hits, maxResults: 2);

        Assert.True(outcome.Truncated);
        Assert.Equal(2, hits.Count);
        Assert.True(hits[0].Span.Start < hits[1].Span.Start);
    }
}
