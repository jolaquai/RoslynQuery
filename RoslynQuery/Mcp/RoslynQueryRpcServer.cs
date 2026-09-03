using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;

using RoslynQuery.Query;

using McpContracts = RoslynQuery.Mcp.Contracts;

namespace RoslynQuery.Mcp;

/// <summary>
/// The pipe side of IRoslynQueryRpc. Translates the wire DTOs in RoslynQuery.Mcp.Contracts to and
/// from the engine's own types and drives QueryEngine/ScopeResolver exactly as the tool window does.
/// </summary>
internal sealed class RoslynQueryRpcServer : McpContracts.IRoslynQueryRpc
{
    private readonly VisualStudioWorkspace _workspace;

    public RoslynQueryRpcServer(VisualStudioWorkspace workspace) => _workspace = workspace;

    public async Task<McpContracts.SearchResponse> SearchAsync(McpContracts.SearchRequest request, CancellationToken cancellationToken)
    {
        var target = ToEngineTarget(request.Target);
        var scope = ToEngineScope(request.Scope);

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var solution = _workspace.CurrentSolution;

        await TaskScheduler.Default;

        var active = request.FilePath is null
            ? null
            : new ActiveContext { FilePath = request.FilePath, Line = request.Line ?? 0, Column = request.Column ?? 0 };

        var units = await ScopeResolver.ResolveAsync(solution, scope, active, request.IncludeGenerated, cancellationToken).ConfigureAwait(false);
        if (units.Count == 0)
            return new McpContracts.SearchResponse { Hits = Array.Empty<McpContracts.HitDto>(), Examined = 0, Errors = 0, Truncated = false };

        var predicate = PredicateCompiler.Compile(target, request.Predicate);

        var hits = new List<McpContracts.HitDto>();
        void OnBatch(IReadOnlyList<QueryHit> batch)
        {
            foreach (var hit in batch) hits.Add(ToDto(hit));
        }

        var outcome = await QueryEngine
            .RunAsync(units, target, request.Predicate, predicate, request.Cap, OnBatch, cancellationToken)
            .ConfigureAwait(false);

        return new McpContracts.SearchResponse
        {
            Hits = hits,
            Examined = outcome.Examined,
            Errors = outcome.Errors,
            FirstError = outcome.FirstError,
            Truncated = outcome.Truncated
        };
    }

    private static McpContracts.HitDto ToDto(QueryHit hit) => new McpContracts.HitDto
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

    // Deliberately explicit rather than a cast through the shared underlying int: the two enums
    // only line up because both lists are written in the same order by hand, and a switch fails
    // loudly if that ever drifts instead of silently mismatching.
    private static TargetKind ToEngineTarget(McpContracts.TargetKind target) => target switch
    {
        McpContracts.TargetKind.SyntaxNode => TargetKind.SyntaxNode,
        McpContracts.TargetKind.SyntaxToken => TargetKind.SyntaxToken,
        McpContracts.TargetKind.Operation => TargetKind.Operation,
        _ => throw new ArgumentOutOfRangeException(nameof(target))
    };

    private static ScopeKind ToEngineScope(McpContracts.ScopeKind scope) => scope switch
    {
        McpContracts.ScopeKind.ContainingMember => ScopeKind.ContainingMember,
        McpContracts.ScopeKind.ContainingType => ScopeKind.ContainingType,
        McpContracts.ScopeKind.Document => ScopeKind.Document,
        McpContracts.ScopeKind.Project => ScopeKind.Project,
        McpContracts.ScopeKind.Solution => ScopeKind.Solution,
        _ => throw new ArgumentOutOfRangeException(nameof(scope))
    };
}
