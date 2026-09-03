using System;
using System.IO.Pipes;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

using RoslynQuery.Mcp;
using RoslynQuery.Mcp.Contracts;

using StreamJsonRpc;

using Xunit;

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
}
