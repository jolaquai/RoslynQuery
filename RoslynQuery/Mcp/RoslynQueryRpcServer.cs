using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;

using RoslynQuery.Mcp.Contracts;
using RoslynQuery.Query;

namespace RoslynQuery.Mcp;

/// <summary>
/// The pipe side of IRoslynQueryRpc, driving QueryEngine/ScopeResolver exactly as the tool window
/// does. Takes the base Workspace type rather than VisualStudioWorkspace: CurrentSolution is
/// thread-safe to read from any thread and nothing here touches live editor state, so there is no
/// VS-UI-thread requirement to inherit - and it means an AdhocWorkspace can drive this in a test the
/// same way one already drives every other engine test in this repo.
/// </summary>
internal sealed class RoslynQueryRpcServer : IRoslynQueryRpc
{
    private readonly Workspace _workspace;

    public RoslynQueryRpcServer(Workspace workspace) => _workspace = workspace;

    public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        var solution = _workspace.CurrentSolution;

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
