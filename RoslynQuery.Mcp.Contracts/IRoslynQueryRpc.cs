using System.Threading;
using System.Threading.Tasks;

namespace RoslynQuery.Mcp.Contracts;

/// <summary>
/// The named-pipe protocol between RoslynQuery.Mcp.Broker and the RoslynQuery VSIX. Search only for
/// this pass - PreviewReplaceAsync/ApplyReplaceAsync land alongside the Replace tool in a later one.
/// </summary>
public interface IRoslynQueryRpc
{
    Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken);
}
