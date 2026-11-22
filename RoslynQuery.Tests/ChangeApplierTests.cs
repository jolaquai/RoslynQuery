using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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

    private static (AdhocWorkspace Workspace, DocumentId First, DocumentId Second) NewTwoDocuments(string first, string second)
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
        var a = workspace.AddDocument(project.Id, "A.cs", SourceText.From(first));
        var b = workspace.AddDocument(project.Id, "B.cs", SourceText.From(second));
        return (workspace, a.Id, b.Id);
    }

    private static async Task<QueryHit> FirstIdentifierHitAsync(Document document, string name)
    {
        var root = await document.GetSyntaxRootAsync(TestContext.Current.CancellationToken);
        var identifier = root.DescendantTokens().First(t => t.IsKind(SyntaxKind.IdentifierToken) && t.Text == name);
        return await HitAtAsync(document, identifier.Span, identifier.Kind().ToString());
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

    [Fact]
    public async Task ApplyAsync_BadlyIndentedReplacement_IsReformattedToMatchContext()
    {
        var (workspace, docId) = NewDocument("class C\r\n{\r\n    void M()\r\n    {\r\n        DoWork();\r\n    }\r\n}");
        var document = workspace.CurrentSolution.GetDocument(docId);
        var root = await document.GetSyntaxRootAsync(TestContext.Current.CancellationToken);
        var statement = root.DescendantNodes().OfType<ExpressionStatementSyntax>().First();
        var hit = await HitAtAsync(document, statement.Span, statement.Kind().ToString());

        // Deliberately flush-left, the way a raw text splice lands regardless of surrounding
        // indentation - this is the shape of replacement Formatter is meant to clean up.
        var item = new ReplacementItem { Hit = hit, Before = "DoWork();", After = "if (Enabled)\r\nFluxMetrics.Add(1);" };
        var outcome = await ChangeApplier.ApplyAsync(workspace, workspace.CurrentSolution, [item], TestContext.Current.CancellationToken);

        Assert.Equal(1, outcome.Applied);
        var lines = (await workspace.CurrentSolution.GetDocument(docId).GetTextAsync(TestContext.Current.CancellationToken))
            .Lines.Select(l => l.ToString()).ToArray();

        var ifLine = Assert.Single(lines, l => l.TrimStart().StartsWith("if (Enabled)"));
        var addLine = Assert.Single(lines, l => l.TrimStart().StartsWith("FluxMetrics.Add(1);"));

        // The added statement should be indented one level deeper than the if it lives under, and
        // both should match the method body's own indentation rather than staying flush-left.
        var leadingWhitespace = (string line) => line.Substring(0, line.Length - line.TrimStart().Length);
        Assert.Equal("        ", leadingWhitespace(ifLine));
        Assert.Equal("            ", leadingWhitespace(addLine));
    }

    [Fact]
    public async Task ApplyAsync_ReplacementUnwrapsASingleStatementBlock_EmbeddedStatementIsIndented()
    {
        // Reproduces unwrapping a single-statement block into a braceless embedded statement, e.g.
        // "if (cond) { Foo(); }" -> "if (cond) Foo();" - the replaced span is the *block*, so none of
        // the "if" itself is part of the new text, and the pre-existing whitespace between the if's
        // condition and the block sits just outside the replacement's own span.
        var (workspace, docId) = NewDocument(
            "class C\r\n{\r\n    void M()\r\n    {\r\n        if (Ready())\r\n        {\r\n            DoWork();\r\n        }\r\n    }\r\n}");
        var document = workspace.CurrentSolution.GetDocument(docId);
        var root = await document.GetSyntaxRootAsync(TestContext.Current.CancellationToken);
        var block = root.DescendantNodes().OfType<BlockSyntax>().First(b => b.Parent is IfStatementSyntax);
        var hit = await HitAtAsync(document, block.Span, block.Kind().ToString());

        var item = new ReplacementItem { Hit = hit, Before = "{ DoWork(); }", After = "DoWork();" };
        var outcome = await ChangeApplier.ApplyAsync(workspace, workspace.CurrentSolution, [item], TestContext.Current.CancellationToken);

        Assert.Equal(1, outcome.Applied);
        var lines = (await workspace.CurrentSolution.GetDocument(docId).GetTextAsync(TestContext.Current.CancellationToken))
            .Lines.Select(l => l.ToString()).ToArray();

        var ifLine = Assert.Single(lines, l => l.TrimStart().StartsWith("if (Ready())"));
        var doWorkLine = Assert.Single(lines, l => l.TrimStart().StartsWith("DoWork();"));

        var leadingWhitespace = (string line) => line.Substring(0, line.Length - line.TrimStart().Length);
        Assert.Equal("        ", leadingWhitespace(ifLine));
        Assert.Equal("            ", leadingWhitespace(doWorkLine));
    }

    [Fact]
    public async Task ApplyAsync_MultipleDocuments_EnrolsEveryChangedDocumentBeforeApplying()
    {
        var (workspace, aId, bId) = NewTwoDocuments("class A { void M() { int x = 1; } }", "class B { void M() { int y = 2; } }");
        var aHit = await FirstIdentifierHitAsync(workspace.CurrentSolution.GetDocument(aId), "x");
        var bHit = await FirstIdentifierHitAsync(workspace.CurrentSolution.GetDocument(bId), "y");

        var enrolled = new List<DocumentId>();
        var textsWhenEnrolled = new List<string>();
        var outcome = await ChangeApplier.ApplyAsync(
            workspace,
            workspace.CurrentSolution,
            [new ReplacementItem { Hit = aHit, Before = "x", After = "ax" },
             new ReplacementItem { Hit = bHit, Before = "y", After = "by" }],
            id =>
            {
                enrolled.Add(id);
                textsWhenEnrolled.Add(workspace.CurrentSolution.GetDocument(id).GetTextAsync().Result.ToString());
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, outcome.Applied);
        Assert.Equal(2, enrolled.Count);
        Assert.Contains(aId, enrolled);
        Assert.Contains(bId, enrolled);

        // Enrolment has to happen while the workspace still holds the pre-apply text, otherwise the
        // buffers join the linked undo after the edit they were meant to group.
        Assert.All(textsWhenEnrolled, t => Assert.DoesNotContain("ax", t));
        Assert.All(textsWhenEnrolled, t => Assert.DoesNotContain("by", t));
    }

    [Fact]
    public async Task ApplyAsync_NothingApplied_DoesNotEnrolAnyDocument()
    {
        var (workspace, docId) = NewDocument("class C { void M() { int x = 1; } }");
        var hit = await FirstIdentifierHitAsync(workspace.CurrentSolution.GetDocument(docId), "x");
        var item = new ReplacementItem { Hit = hit, Before = "x", After = "renamed", Included = false };

        var enrolled = new List<DocumentId>();
        var outcome = await ChangeApplier.ApplyAsync(
            workspace, workspace.CurrentSolution, [item], enrolled.Add, TestContext.Current.CancellationToken);

        Assert.Equal(0, outcome.Applied);
        Assert.Empty(enrolled);
    }
}

public class ChangeApplierLinkedTests
{
    private const string Source = "class C\r\n{\r\n#if B\r\n    void M1() { }\r\n#endif\r\n    void M2() { }\r\n}";

    private static (AdhocWorkspace Workspace, DocumentId Plain, DocumentId WithB) Linked()
    {
        var workspace = new AdhocWorkspace();
        var ids = new List<DocumentId>();
        foreach (var symbols in new[] { new string[0], new[] { "B" } })
        {
            var project = workspace.AddProject(ProjectInfo.Create(
                ProjectId.CreateNewId(), VersionStamp.Create(), "Linked", "Linked", LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                parseOptions: CSharpParseOptions.Default.WithPreprocessorSymbols(symbols),
                metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]));

            ids.Add(workspace.AddDocument(DocumentInfo.Create(
                DocumentId.CreateNewId(project.Id), "Linked.cs",
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From(Source), VersionStamp.Create())),
                filePath: @"C:\src\Linked.cs")).Id);
        }

        return (workspace, ids[0], ids[1]);
    }

    private static async Task<ReplacementItem> RenameAsync(Solution solution, DocumentId id, string name, string to)
    {
        var document = solution.GetDocument(id);
        var root = await document.GetSyntaxRootAsync(TestContext.Current.CancellationToken);
        var token = root.DescendantTokens().First(t => t.IsKind(SyntaxKind.IdentifierToken) && t.Text == name);
        var text = await document.GetTextAsync(TestContext.Current.CancellationToken);
        return new ReplacementItem { Hit = QueryHit.Create(document, text, token.Span, "IdentifierToken", TargetKind.SyntaxToken), Before = name, After = to };
    }

    [Fact]
    public async Task ApplyAsync_HitsInTwoLinkedCopiesOfOneFile_KeepsBothEdits()
    {
        var (workspace, plain, withB) = Linked();
        var solution = workspace.CurrentSolution;
        var items = new[] { await RenameAsync(solution, withB, "M1", "One"), await RenameAsync(solution, plain, "M2", "Two") };

        var outcome = await ChangeApplier.ApplyAsync(workspace, solution, items, TestContext.Current.CancellationToken);

        var text = (await workspace.CurrentSolution.GetDocument(plain).GetTextAsync(TestContext.Current.CancellationToken)).ToString();
        Assert.Equal(2, outcome.Applied);
        Assert.Contains("One()", text);
        Assert.Contains("Two()", text);
    }
}

public class ChangeApplierLinkedPlanTests
{
    [Fact]
    public async Task PlanAsync_HitsInTwoLinkedCopies_EveryChangedDocumentCarriesBothEdits()
    {
        var workspace = new AdhocWorkspace();
        const string source = "class C\r\n{\r\n#if B\r\n    void M1() { }\r\n#endif\r\n    void M2() { }\r\n}";
        var ids = new List<DocumentId>();
        foreach (var symbols in new[] { new string[0], new[] { "B" } })
        {
            var project = workspace.AddProject(ProjectInfo.Create(
                ProjectId.CreateNewId(), VersionStamp.Create(), "Linked", "Linked", LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                parseOptions: CSharpParseOptions.Default.WithPreprocessorSymbols(symbols),
                metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]));
            ids.Add(workspace.AddDocument(DocumentInfo.Create(
                DocumentId.CreateNewId(project.Id), "Linked.cs",
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From(source), VersionStamp.Create())),
                filePath: @"C:\src\Linked.cs")).Id);
        }

        var solution = workspace.CurrentSolution;
        var items = new List<ReplacementItem>();
        foreach (var (id, name, to) in new[] { (ids[1], "M1", "One"), (ids[0], "M2", "Two") })
        {
            var document = solution.GetDocument(id);
            var root = await document.GetSyntaxRootAsync(TestContext.Current.CancellationToken);
            var token = root.DescendantTokens().First(t => t.IsKind(SyntaxKind.IdentifierToken) && t.Text == name);
            var text = await document.GetTextAsync(TestContext.Current.CancellationToken);
            items.Add(new ReplacementItem { Hit = QueryHit.Create(document, text, token.Span, "IdentifierToken", TargetKind.SyntaxToken), Before = name, After = to });
        }

        var plan = await ChangeApplier.PlanAsync(workspace, solution, items, TestContext.Current.CancellationToken);

        foreach (var id in plan.ChangedDocuments)
        {
            var text = (await plan.Solution.GetDocument(id).GetTextAsync(TestContext.Current.CancellationToken)).ToString();
            Assert.Contains("One()", text);
            Assert.Contains("Two()", text);
        }
    }
}

public class ChangeApplierCommitTests
{
    private static async Task<(AdhocWorkspace Workspace, DocumentId Id, ChangeApplier.ApplyPlan Plan)> PlannedAsync()
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject(ProjectInfo.Create(
            ProjectId.CreateNewId(), VersionStamp.Create(), "P", "P", LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]));
        var document = workspace.AddDocument(project.Id, "A.cs", SourceText.From("class C { int x; }"));

        var root = await document.GetSyntaxRootAsync(TestContext.Current.CancellationToken);
        var token = root.DescendantTokens().First(t => t.IsKind(SyntaxKind.IdentifierToken) && t.Text == "x");
        var text = await document.GetTextAsync(TestContext.Current.CancellationToken);
        var item = new ReplacementItem { Hit = QueryHit.Create(document, text, token.Span, "IdentifierToken", TargetKind.SyntaxToken), Before = "x", After = "renamed" };

        var plan = await ChangeApplier.PlanAsync(workspace, workspace.CurrentSolution, [item], TestContext.Current.CancellationToken);
        return (workspace, document.Id, plan);
    }

    [Fact]
    public async Task CommitAsync_UntouchedDocument_Writes()
    {
        var (workspace, id, plan) = await PlannedAsync();

        var outcome = await ChangeApplier.CommitAsync(workspace, plan, null, TestContext.Current.CancellationToken);

        Assert.Equal(1, outcome.Applied);
        Assert.Contains("renamed", (await workspace.CurrentSolution.GetDocument(id).GetTextAsync(TestContext.Current.CancellationToken)).ToString());
    }

    [Fact]
    public async Task CommitAsync_DocumentEditedAfterThePlanWasMade_RefusesAndKeepsTheEdit()
    {
        var (workspace, id, plan) = await PlannedAsync();
        Assert.True(workspace.TryApplyChanges(workspace.CurrentSolution.WithDocumentText(id, SourceText.From("class C { int x; /* typed meanwhile */ }"))));

        var outcome = await ChangeApplier.CommitAsync(workspace, plan, null, TestContext.Current.CancellationToken);

        Assert.Equal(0, outcome.Applied);
        Assert.Contains(outcome.Warnings, w => w.Contains("edited while"));
        Assert.Contains("typed meanwhile", (await workspace.CurrentSolution.GetDocument(id).GetTextAsync(TestContext.Current.CancellationToken)).ToString());
    }
}
