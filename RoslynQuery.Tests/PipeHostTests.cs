using System;
using System.IO.Pipes;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

using RoslynQuery.Mcp;
using RoslynQuery.Mcp.Contracts;

using StreamJsonRpc;

using Xunit;

// The tests run with no VS main thread, so PipeHost is deliberately constructed without a JoinableTaskFactory.
#pragma warning disable VSTHRD012

namespace RoslynQuery.Tests;

// SearchAsync compiles the predicate through PredicateCompiler, whose cache is the same process-wide
// static shared with every other test that compiles one - see PredicateCompilerCacheCollection.
// This is the one test class that drives the pipe/JsonRpc plumbing itself rather than calling into
// QueryEngine directly - CI otherwise only ever proves the MCP bridge still compiles.
[Collection(PredicateCompilerCacheCollection.Name)]
public class PipeHostTests
{
    private static Workspace WorkspaceWithDocument(string source)
    {
        var workspace = new AdhocWorkspace();
        var projectInfo = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Create(),
            "PipeHostTestProject",
            "PipeHostTestProject",
            LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);

        var project = workspace.AddProject(projectInfo);
        workspace.AddDocument(project.Id, "Test.cs", SourceText.From(source));
        return workspace;
    }

    private static async Task<IRoslynQueryRpc> ConnectAsync(PipeHost host, NamedPipeClientStream client)
    {
        host.Start();
        await client.ConnectAsync(5000, TestContext.Current.CancellationToken);
        return JsonRpc.Attach<IRoslynQueryRpc>(client);
    }

    [Fact]
    public async Task SearchAsync_OverARealNamedPipe_RoundTripsAMatchAsAHitDto()
    {
        var workspace = WorkspaceWithDocument("class C { void M() { if (true) { } } }");
        var pipeName = $"RoslynQueryTest.{Guid.NewGuid():N}";

        using var host = new PipeHost(pipeName, workspace);
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        var rpc = await ConnectAsync(host, client);

        var response = await rpc.SearchAsync(
            new SearchRequest
            {
                Target = TargetKind.SyntaxNode,
                Scope = ScopeKind.Solution,
                Predicate = "n.IsKind(SyntaxKind.IfStatement)",
                Cap = 100
            },
            TestContext.Current.CancellationToken);

        var hit = Assert.Single(response.Hits);
        Assert.Equal("IfStatement", hit.Kind);
        Assert.Equal(0, response.Errors);
        Assert.False(response.Truncated);
    }

    [Fact]
    public async Task SearchAsync_NothingMatches_ReturnsAnEmptyResponseNotAFault()
    {
        var workspace = WorkspaceWithDocument("class C { void M() { } }");
        var pipeName = $"RoslynQueryTest.{Guid.NewGuid():N}";

        using var host = new PipeHost(pipeName, workspace);
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        var rpc = await ConnectAsync(host, client);

        var response = await rpc.SearchAsync(
            new SearchRequest { Target = TargetKind.SyntaxNode, Scope = ScopeKind.Solution, Predicate = "n.IsKind(SyntaxKind.IfStatement)", Cap = 100 },
            TestContext.Current.CancellationToken);

        Assert.Empty(response.Hits);
        Assert.Equal(0, response.Errors);
    }

    private static ReplacePreviewRequest PreviewRequest(string predicate, string replacement) => new ReplacePreviewRequest
    {
        Search = new SearchRequest { Target = TargetKind.SyntaxToken, Scope = ScopeKind.Solution, Predicate = predicate, Cap = 100 },
        Replacement = replacement
    };

    [Fact]
    public async Task PreviewReplaceAsync_OverThePipe_ReturnsAPreviewIdAndBeforeAfterItems()
    {
        var workspace = WorkspaceWithDocument("class C { int F() { return 1; } }");
        var pipeName = $"RoslynQueryTest.{Guid.NewGuid():N}";

        using var host = new PipeHost(pipeName, workspace);
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        var rpc = await ConnectAsync(host, client);

        var response = await rpc.PreviewReplaceAsync(
            PreviewRequest("t.IsKind(SyntaxKind.NumericLiteralToken)", "\"2\""),
            TestContext.Current.CancellationToken);

        Assert.NotNull(response.PreviewId);
        Assert.Equal(1, response.IncludedCount);
        var item = Assert.Single(response.Items);
        Assert.Equal(0, item.Index);
        Assert.Equal("1", item.Before);
        Assert.Equal("2", item.After);
        Assert.Null(item.Warning);
        Assert.True(item.Included);
    }

    [Fact]
    public async Task PreviewReplaceAsync_NothingMatches_ReturnsNoPreviewId()
    {
        var workspace = WorkspaceWithDocument("class C { int F() { return 1; } }");
        var pipeName = $"RoslynQueryTest.{Guid.NewGuid():N}";

        using var host = new PipeHost(pipeName, workspace);
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        var rpc = await ConnectAsync(host, client);

        var response = await rpc.PreviewReplaceAsync(
            PreviewRequest("t.IsKind(SyntaxKind.StringLiteralToken)", "\"x\""),
            TestContext.Current.CancellationToken);

        Assert.Null(response.PreviewId);
        Assert.Empty(response.Items);
    }

    [Fact]
    public async Task ApplyReplaceAsync_WithAFreshPreviewId_WritesTheChangeBackToTheWorkspace()
    {
        var workspace = WorkspaceWithDocument("class C { int F() { return 1; } }");
        var pipeName = $"RoslynQueryTest.{Guid.NewGuid():N}";

        using var host = new PipeHost(pipeName, workspace);
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        var rpc = await ConnectAsync(host, client);

        var preview = await rpc.PreviewReplaceAsync(
            PreviewRequest("t.IsKind(SyntaxKind.NumericLiteralToken)", "\"2\""),
            TestContext.Current.CancellationToken);

        var apply = await rpc.ApplyReplaceAsync(
            new ReplaceApplyRequest { PreviewId = preview.PreviewId },
            TestContext.Current.CancellationToken);

        Assert.True(apply.Found);
        Assert.Equal(1, apply.Applied);
        Assert.Equal(0, apply.Skipped);

        var document = workspace.CurrentSolution.Projects.Single().Documents.Single();
        var text = await document.GetTextAsync(TestContext.Current.CancellationToken);
        Assert.Contains("return 2;", text.ToString());

        // The id is spent once anything applied.
        var again = await rpc.ApplyReplaceAsync(
            new ReplaceApplyRequest { PreviewId = preview.PreviewId },
            TestContext.Current.CancellationToken);
        Assert.False(again.Found);
    }

    [Fact]
    public async Task ApplyReplaceAsync_WithAnUnknownPreviewId_ReturnsFoundFalse()
    {
        var workspace = WorkspaceWithDocument("class C { int F() { return 1; } }");
        var pipeName = $"RoslynQueryTest.{Guid.NewGuid():N}";

        using var host = new PipeHost(pipeName, workspace);
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        var rpc = await ConnectAsync(host, client);

        var apply = await rpc.ApplyReplaceAsync(
            new ReplaceApplyRequest { PreviewId = "does-not-exist" },
            TestContext.Current.CancellationToken);

        Assert.False(apply.Found);
        Assert.Equal(0, apply.Applied);
    }

    [Fact]
    public async Task ApplyReplaceAsync_WithAnEmptyIndexSet_AppliesNothing()
    {
        var workspace = WorkspaceWithDocument("class C { int F() { return 1; } }");
        var pipeName = $"RoslynQueryTest.{Guid.NewGuid():N}";

        using var host = new PipeHost(pipeName, workspace);
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        var rpc = await ConnectAsync(host, client);

        var preview = await rpc.PreviewReplaceAsync(
            PreviewRequest("t.IsKind(SyntaxKind.NumericLiteralToken)", "\"2\""),
            TestContext.Current.CancellationToken);

        var apply = await rpc.ApplyReplaceAsync(
            new ReplaceApplyRequest { PreviewId = preview.PreviewId, Indices = Array.Empty<int>() },
            TestContext.Current.CancellationToken);

        Assert.True(apply.Found);
        Assert.Equal(0, apply.Applied);

        var document = workspace.CurrentSolution.Projects.Single().Documents.Single();
        var text = await document.GetTextAsync(TestContext.Current.CancellationToken);
        Assert.Contains("return 1;", text.ToString());
    }
}
