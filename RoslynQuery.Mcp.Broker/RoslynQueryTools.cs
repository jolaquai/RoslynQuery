using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

using ModelContextProtocol.Server;

using RoslynQuery.Mcp.Contracts;

namespace RoslynQuery.Mcp.Broker;

/// <summary>
/// The MCP tool surface. Each method is a thin call across the pipe - rpc is DI-injected with the
/// JsonRpc proxy Program.cs attaches at startup, not part of the tool call's own JSON arguments.
/// </summary>
[McpServerToolType]
public static class RoslynQueryTools
{
    [McpServerTool(Name = "roslynquery_search")]
    [Description("Search the open Visual Studio solution for SyntaxNodes, SyntaxTokens or IOperations matching a C# predicate.")]
    public static Task<SearchResponse> Search(IRoslynQueryRpc rpc, SearchRequest request, CancellationToken cancellationToken)
        => rpc.SearchAsync(request, cancellationToken);
}
