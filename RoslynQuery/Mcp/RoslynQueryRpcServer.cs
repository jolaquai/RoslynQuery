using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.Threading;

using RoslynQuery.Mcp.Contracts;
using RoslynQuery.Query;
using RoslynQuery.Replace;

namespace RoslynQuery.Mcp;

/// <summary>
/// The pipe side of IRoslynQueryRpc, driving QueryEngine/ScopeResolver/ReplaceEngine exactly as the
/// tool window does. Takes the base Workspace type rather than VisualStudioWorkspace: CurrentSolution
/// is thread-safe to read from any thread and Search touches no live editor state, so an
/// AdhocWorkspace can drive this in a test the same way it drives every other engine test here.
/// Replace-apply is the exception - it writes back through the workspace, which is UI-thread-only for
/// VisualStudioWorkspace - so <paramref name="mainThread"/> (null in tests) is used to marshal that
/// one call.
/// </summary>
internal sealed class RoslynQueryRpcServer : IRoslynQueryRpc
{
    private readonly Workspace _workspace;
    private readonly JoinableTaskFactory _mainThread;
    private readonly PreviewCache _previews = new PreviewCache();

    public RoslynQueryRpcServer(Workspace workspace, JoinableTaskFactory mainThread = null)
    {
        _workspace = workspace;
        _mainThread = mainThread;
    }

    public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        var (hits, outcome) = await RunSearchAsync(_workspace.CurrentSolution, request, cancellationToken).ConfigureAwait(false);
        if (outcome is null)
            return new SearchResponse { Hits = Array.Empty<HitDto>(), Examined = 0, Errors = 0, Truncated = false };

        var dtos = new HitDto[hits.Count];
        for (var i = 0; i < hits.Count; i++) dtos[i] = ToDto(hits[i]);

        return new SearchResponse
        {
            Hits = dtos,
            Examined = outcome.Examined,
            Errors = outcome.Errors,
            FirstError = outcome.FirstError,
            Truncated = outcome.Truncated
        };
    }

    public async Task<ReplacePreviewResponse> PreviewReplaceAsync(ReplacePreviewRequest request, CancellationToken cancellationToken)
    {
        var search = request?.Search ?? new SearchRequest();
        var solution = _workspace.CurrentSolution;

        var (hits, outcome) = await RunSearchAsync(solution, search, cancellationToken).ConfigureAwait(false);
        if (outcome is null || hits.Count == 0)
        {
            return new ReplacePreviewResponse
            {
                PreviewId = null,
                Items = Array.Empty<ReplacementPreviewDto>(),
                Examined = outcome?.Examined ?? 0,
                Errors = outcome?.Errors ?? 0,
                FirstError = outcome?.FirstError,
                Truncated = outcome?.Truncated ?? false,
                IncludedCount = 0
            };
        }

        var replace = ReplaceCompiler.Compile(search.Target, request.Replacement);

        var items = await ReplaceEngine.GenerateAsync(solution, hits, search.Target, replace, cancellationToken).ConfigureAwait(false);
        ReplaceEngine.MarkConflicts(items);

        var dtos = new ReplacementPreviewDto[items.Count];
        var included = 0;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var isIncluded = item.Included && item.After != null && item.Warning is null;
            if (isIncluded) included++;

            dtos[i] = new ReplacementPreviewDto
            {
                Index = i,
                FilePath = item.Hit.FilePath,
                FileName = item.Hit.FileName,
                Line = item.Hit.Line,
                Column = item.Hit.Column,
                Kind = item.Hit.Kind,
                Before = item.Before,
                After = item.After,
                Warning = item.Warning,
                Included = isIncluded
            };
        }

        var previewId = included > 0 ? _previews.Add(solution, items, search.Target) : null;

        return new ReplacePreviewResponse
        {
            PreviewId = previewId,
            Items = dtos,
            Examined = outcome.Examined,
            Errors = outcome.Errors,
            FirstError = outcome.FirstError,
            Truncated = outcome.Truncated,
            IncludedCount = included
        };
    }

    public async Task<ReplaceApplyResponse> ApplyReplaceAsync(ReplaceApplyRequest request, CancellationToken cancellationToken)
    {
        if (!_previews.TryGet(request?.PreviewId, out var entry))
            return new ReplaceApplyResponse { Found = false, Warnings = Array.Empty<string>() };

        if (request.Indices != null)
        {
            var wanted = new HashSet<int>(request.Indices);
            for (var i = 0; i < entry.Items.Count; i++)
                entry.Items[i].Included = wanted.Contains(i);
        }

        // TryApplyChanges and the text-buffer touches inside ChangeApplier are UI-thread-only for a
        // real VisualStudioWorkspace; the continuation stays on the main thread, which the RPC
        // completion does not care about. Null in tests, where an AdhocWorkspace has no such rule.
        if (_mainThread != null)
            await _mainThread.SwitchToMainThreadAsync(cancellationToken);

        var outcome = await ChangeApplier.ApplyAsync(_workspace, entry.Solution, entry.Items, cancellationToken).ConfigureAwait(true);

        if (outcome.Applied > 0)
            _previews.Remove(request.PreviewId);

        return new ReplaceApplyResponse
        {
            Found = true,
            Applied = outcome.Applied,
            Skipped = outcome.Skipped,
            Warnings = outcome.Warnings
        };
    }

    private static async Task<(IReadOnlyList<QueryHit> Hits, QueryOutcome Outcome)> RunSearchAsync(
        Solution solution, SearchRequest request, CancellationToken cancellationToken)
    {
        var active = request.FilePath is null
            ? null
            : new ActiveContext { FilePath = request.FilePath, Line = request.Line ?? 0, Column = request.Column ?? 0 };

        var units = await ScopeResolver.ResolveAsync(solution, request.Scope, active, request.IncludeGenerated, cancellationToken).ConfigureAwait(false);
        if (units.Count == 0) return (Array.Empty<QueryHit>(), null);

        var predicate = PredicateCompiler.Compile(request.Target, request.Predicate);

        var hits = new List<QueryHit>();
        void OnBatch(IReadOnlyList<QueryHit> batch)
        {
            // QueryEngine calls this from several scanning threads at once.
            lock (hits) hits.AddRange(batch);
        }

        var outcome = await QueryEngine
            .RunAsync(units, request.Target, request.Predicate, predicate, request.Cap, OnBatch, cancellationToken)
            .ConfigureAwait(false);

        return (hits, outcome);
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
