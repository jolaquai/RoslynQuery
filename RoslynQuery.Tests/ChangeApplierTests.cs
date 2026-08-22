using System.Linq;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

using RoslynQuery.Query;
using RoslynQuery.Replace;

using Xunit;

namespace RoslynQuery.Tests;

public class ChangeApplierTests
{
    private static (AdhocWorkspace Workspace, DocumentId DocumentId) NewDocument(string source)
    {
        var workspace = new AdhocWorkspace();
        var projectInfo = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Create(),
            "ChangeApplierTestProject",
            "ChangeApplierTestProject",
            LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);

        var project = workspace.AddProject(projectInfo);
        var document = workspace.AddDocument(project.Id, "Test.cs", SourceText.From(source));
        return (workspace, document.Id);
    }

    private static async Task<QueryHit> HitAtAsync(Document document, TextSpan span, string kind)
    {
        var text = await document.GetTextAsync(TestContext.Current.CancellationToken);
        return QueryHit.Create(document, text, span, kind, TargetKind.SyntaxToken);
    }

    [Fact]
    public async Task ApplyAsync_SingleStringReplacement_UpdatesDocumentText()
    {
        var (workspace, docId) = NewDocument("class C { void M() { int x = 1; } }");
        var document = workspace.CurrentSolution.GetDocument(docId);
        var root = await document.GetSyntaxRootAsync(TestContext.Current.CancellationToken);
        var identifier = root.DescendantTokens().First(t => t.IsKind(SyntaxKind.IdentifierToken) && t.Text == "x");

        var hit = await HitAtAsync(document, identifier.Span, identifier.Kind().ToString());
        var item = new ReplacementItem { Hit = hit, Before = "x", After = "renamed" };

        var outcome = await ChangeApplier.ApplyAsync(workspace, workspace.CurrentSolution, [item], TestContext.Current.CancellationToken);

        Assert.Equal(1, outcome.Applied);
        Assert.Equal(0, outcome.Skipped);
        var newText = await workspace.CurrentSolution.GetDocument(docId).GetTextAsync(TestContext.Current.CancellationToken);
        Assert.Contains("int renamed = 1;", newText.ToString());
    }

    [Fact]
    public async Task ApplyAsync_ExcludedItem_ChangesNothing()
    {
        var (workspace, docId) = NewDocument("class C { void M() { int x = 1; } }");
        var document = workspace.CurrentSolution.GetDocument(docId);
        var root = await document.GetSyntaxRootAsync(TestContext.Current.CancellationToken);
        var identifier = root.DescendantTokens().First(t => t.IsKind(SyntaxKind.IdentifierToken) && t.Text == "x");

        var hit = await HitAtAsync(document, identifier.Span, identifier.Kind().ToString());
        var item = new ReplacementItem { Hit = hit, Before = "x", After = "renamed", Included = false };

        var outcome = await ChangeApplier.ApplyAsync(workspace, workspace.CurrentSolution, [item], TestContext.Current.CancellationToken);

        Assert.Equal(0, outcome.Applied);
        Assert.Equal(0, outcome.Skipped);
        var newText = await workspace.CurrentSolution.GetDocument(docId).GetTextAsync(TestContext.Current.CancellationToken);
        Assert.Equal("class C { void M() { int x = 1; } }", newText.ToString());
    }

    [Fact]
    public async Task ApplyAsync_TwoNonOverlappingHitsInOneDocument_AppliesBoth()
    {
        var (workspace, docId) = NewDocument("class C { void M() { int x = 1; int y = 2; } }");
        var document = workspace.CurrentSolution.GetDocument(docId);
        var root = await document.GetSyntaxRootAsync(TestContext.Current.CancellationToken);
        var identifiers = root.DescendantTokens().Where(t => t.IsKind(SyntaxKind.IdentifierToken)).ToArray();

        var items = new[]
        {
            new ReplacementItem { Hit = await HitAtAsync(document, identifiers[0].Span, identifiers[0].Kind().ToString()), Before = identifiers[0].Text, After = "renamedX" },
            new ReplacementItem { Hit = await HitAtAsync(document, identifiers[1].Span, identifiers[1].Kind().ToString()), Before = identifiers[1].Text, After = "renamedY" },
        };

        var outcome = await ChangeApplier.ApplyAsync(workspace, workspace.CurrentSolution, items, TestContext.Current.CancellationToken);

        Assert.Equal(2, outcome.Applied);
        var newText = await workspace.CurrentSolution.GetDocument(docId).GetTextAsync(TestContext.Current.CancellationToken);
        Assert.Contains("renamedX", newText.ToString());
        Assert.Contains("renamedY", newText.ToString());
    }

    [Fact]
    public async Task ApplyAsync_OverlappingHits_SkipsTheSecondAndAppliesFirst()
    {
        var (workspace, docId) = NewDocument("class C { void M() { int xy = 1; } }");
        var document = workspace.CurrentSolution.GetDocument(docId);
        var root = await document.GetSyntaxRootAsync(TestContext.Current.CancellationToken);
        var identifier = root.DescendantTokens().First(t => t.IsKind(SyntaxKind.IdentifierToken) && t.Text == "xy");

        var whole = identifier.Span;
        var overlapping = new TextSpan(whole.Start, whole.Length - 1);

        var items = new[]
        {
            new ReplacementItem { Hit = await HitAtAsync(document, whole, identifier.Kind().ToString()), Before = "xy", After = "first" },
            new ReplacementItem { Hit = await HitAtAsync(document, overlapping, identifier.Kind().ToString()), Before = "x", After = "second" },
        };

        var outcome = await ChangeApplier.ApplyAsync(workspace, workspace.CurrentSolution, items, TestContext.Current.CancellationToken);

        Assert.Equal(1, outcome.Applied);
        Assert.Equal(1, outcome.Skipped);
        Assert.Contains(outcome.Warnings, w => w.Contains("overlaps"));
    }

    [Fact]
    public async Task ApplyAsync_DocumentShrunkPastTheMatch_IsSkippedAsStale()
    {
        var (workspace, docId) = NewDocument("class C { void M() { int x = 1; } }");
        var original = workspace.CurrentSolution.GetDocument(docId);
        var root = await original.GetSyntaxRootAsync(TestContext.Current.CancellationToken);
        var identifier = root.DescendantTokens().First(t => t.IsKind(SyntaxKind.IdentifierToken) && t.Text == "x");
        var hit = await HitAtAsync(original, identifier.Span, identifier.Kind().ToString());
        var ranAgainst = workspace.CurrentSolution;

        // Truncate the document to well before the match's recorded position.
        var truncated = workspace.CurrentSolution.WithDocumentText(docId, SourceText.From("class C { }"));
        Assert.True(workspace.TryApplyChanges(truncated));

        var item = new ReplacementItem { Hit = hit, Before = "x", After = "renamed" };
        var outcome = await ChangeApplier.ApplyAsync(workspace, ranAgainst, [item], TestContext.Current.CancellationToken);

        Assert.Equal(0, outcome.Applied);
        Assert.Equal(1, outcome.Skipped);
        Assert.Contains(outcome.Warnings, w => w.Contains("stale"));
    }

    [Fact]
    public async Task ApplyAsync_DocumentEditedSinceSearch_RemapsTheSpanForward()
    {
        var (workspace, docId) = NewDocument("class C { void M() { int x = 1; } }");
        var original = workspace.CurrentSolution.GetDocument(docId);
        var root = await original.GetSyntaxRootAsync(TestContext.Current.CancellationToken);
        var identifier = root.DescendantTokens().First(t => t.IsKind(SyntaxKind.IdentifierToken) && t.Text == "x");
        var hit = await HitAtAsync(original, identifier.Span, identifier.Kind().ToString());
        var ranAgainst = workspace.CurrentSolution;

        // Simulate an edit made after Search ran but before Apply: insert text above the match,
        // shifting its position without changing its content.
        var originalText = await original.GetTextAsync(TestContext.Current.CancellationToken);
        var editedText = originalText.Replace(new TextSpan(0, 0), "// a leading comment\r\n");
        var editedSolution = workspace.CurrentSolution.WithDocumentText(docId, editedText);
        Assert.True(workspace.TryApplyChanges(editedSolution));

        var item = new ReplacementItem { Hit = hit, Before = "x", After = "renamed" };
        var outcome = await ChangeApplier.ApplyAsync(workspace, ranAgainst, [item], TestContext.Current.CancellationToken);

        Assert.Equal(1, outcome.Applied);
        var finalText = await workspace.CurrentSolution.GetDocument(docId).GetTextAsync(TestContext.Current.CancellationToken);
        Assert.Contains("int renamed = 1;", finalText.ToString());
        Assert.Contains("// a leading comment", finalText.ToString());
    }
}
