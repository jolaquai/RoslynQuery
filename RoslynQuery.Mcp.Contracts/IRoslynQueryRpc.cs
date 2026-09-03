using System.Threading;
using System.Threading.Tasks;

namespace RoslynQuery.Mcp.Contracts;

/// <summary>
/// The named-pipe protocol between RoslynQuery.Mcp.Broker and the RoslynQuery VSIX. Replace is a
/// two-step preview-then-apply flow: <see cref="PreviewReplaceAsync"/> caches the generated
/// transforms against the search snapshot and returns a PreviewId, which <see cref="ApplyReplaceAsync"/>
/// redeems to commit a chosen subset.
/// </summary>
public interface IRoslynQueryRpc
{
    Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken);

    Task<ReplacePreviewResponse> PreviewReplaceAsync(ReplacePreviewRequest request, CancellationToken cancellationToken);

    Task<ReplaceApplyResponse> ApplyReplaceAsync(ReplaceApplyRequest request, CancellationToken cancellationToken);
}
