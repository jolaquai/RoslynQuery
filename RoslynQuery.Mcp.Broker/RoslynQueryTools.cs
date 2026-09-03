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

    [McpServerTool(Name = "roslynquery_replace_preview")]
    [Description("Run a search over the open Visual Studio solution and preview a C# transform applied to every match. "
        + "Returns a PreviewId plus a before/after item per match; nothing is written. Pass the PreviewId to "
        + "roslynquery_replace_apply to commit a chosen subset.")]
    public static Task<ReplacePreviewResponse> ReplacePreview(IRoslynQueryRpc rpc, ReplacePreviewRequest request, CancellationToken cancellationToken)
        => rpc.PreviewReplaceAsync(request, cancellationToken);

    [McpServerTool(Name = "roslynquery_replace_apply")]
    [Description("Commit a replace preview created by roslynquery_replace_preview. Applies every default-included item, "
        + "or exactly the items named in Indices. The PreviewId is single-use once anything applies, and expires after "
        + "a few minutes.")]
    public static Task<ReplaceApplyResponse> ReplaceApply(IRoslynQueryRpc rpc, ReplaceApplyRequest request, CancellationToken cancellationToken)
        => rpc.ApplyReplaceAsync(request, cancellationToken);
}
