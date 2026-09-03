using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

using RoslynQuery.Mcp.Contracts;
using RoslynQuery.Query;

namespace RoslynQuery.Mcp;

/// <summary>
/// The pipe side of IRoslynQueryRpc, driving QueryEngine/ScopeResolver exactly as the tool window
/// does. TargetKind/ScopeKind are RoslynQuery.Mcp.Contracts' own types, so the engine takes a
/// request's Target/Scope directly - there's no translation layer at this boundary.
/// </summary>
internal sealed class RoslynQueryRpcServer : IRoslynQueryRpc
{
    private readonly VisualStudioWorkspace _workspace;

    public RoslynQueryRpcServer(VisualStudioWorkspace workspace) => _workspace = workspace;

    public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var solution = _workspace.CurrentSolution;

        await TaskScheduler.Default;

        var active = request.FilePath is null
            ? null
            : new ActiveContext { FilePath = request.FilePath, Line = request.Line ?? 0, Column = request.Column ?? 0 };

        var units = await ScopeResolver.ResolveAsync(solution, request.Scope, active, request.IncludeGenerated, cancellationToken).ConfigureAwait(false);
        if (units.Count == 0)
            return new SearchResponse { Hits = Array.Empty<HitDto>(), Examined = 0, Errors = 0, Truncated = false };

        var predicate = PredicateCompiler.Compile(request.Target, request.Predicate);

        var hits = new List<HitDto>();
        void OnBatch(IReadOnlyList<QueryHit> batch)
        {
            foreach (var hit in batch) hits.Add(ToDto(hit));
        }

        var outcome = await QueryEngine
            .RunAsync(units, request.Target, request.Predicate, predicate, request.Cap, OnBatch, cancellationToken)
            .ConfigureAwait(false);

        return new SearchResponse
        {
            Hits = hits,
            Examined = outcome.Examined,
            Errors = outcome.Errors,
            FirstError = outcome.FirstError,
            Truncated = outcome.Truncated
        };
    }

    private static HitDto ToDto(QueryHit hit) => new HitDto
    {
        FilePath = hit.FilePath,
        FileName = hit.FileName,
        Line = hit.Line,
        Column = hit.Column,
        EndLine = hit.EndLine,
        EndColumn = hit.EndColumn,
        Kind = hit.Kind,
        Preview = hit.Preview
    };
}
