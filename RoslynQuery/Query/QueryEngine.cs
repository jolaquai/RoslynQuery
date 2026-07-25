using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace RoslynQuery.Query;

internal sealed class QueryOutcome
{
    public int Examined { get; set; }
    public int Matched { get; set; }
    public int Errors { get; set; }
    public int Documents { get; set; }
    public string FirstError { get; set; }
    public bool Truncated { get; set; }
    public TimeSpan Elapsed { get; set; }
}

internal static class QueryEngine
{
    private const int BatchSize = 200;

    // Building a SemanticModel for every document is the dominant cost of a wide run, so it is
    // only paid when the expression actually names `model` (IOperation always needs one).
    private static readonly Regex MentionsModel = new Regex(@"\bmodel\b", RegexOptions.Compiled);

    public static async Task<QueryOutcome> RunAsync(
        IReadOnlyList<ScopeUnit> units,
        TargetKind target,
        string expression,
        Delegate predicate,
        int maxResults,
        Action<IReadOnlyList<QueryHit>> onBatch,
        CancellationToken cancellationToken)
    {
        var outcome = new QueryOutcome { Documents = units.Count };
        var watch = Stopwatch.StartNew();
        var needsModel = target == TargetKind.Operation || MentionsModel.IsMatch(expression ?? string.Empty);

        var pending = new List<QueryHit>(BatchSize);
        var sync = new object();
        var examined = 0;
        var matched = 0;
        var errors = 0;
        string firstError = null;

        using (var cap = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            var token = cap.Token;

            void Flush(bool force)
            {
                List<QueryHit> batch = null;
                lock (sync)
                {
                    if (pending.Count >= BatchSize || (force && pending.Count > 0))
                    {
                        batch = new List<QueryHit>(pending);
                        pending.Clear();
                    }
                }

                if (batch != null) onBatch(batch);
            }

            void Emit(QueryHit hit)
            {
                if (Interlocked.Increment(ref matched) > maxResults)
                {
                    outcome.Truncated = true;
                    cap.Cancel();
                    return;
                }

                lock (sync) pending.Add(hit);
                Flush(false);
            }

            void Fail(Exception ex)
            {
                Interlocked.Increment(ref errors);
                if (firstError is null) Interlocked.CompareExchange(ref firstError, ex.GetType().Name + ": " + ex.Message, null);
            }

            using (var gate = new SemaphoreSlim(Environment.ProcessorCount))
            {
                var work = units.Select(async unit =>
                {
                    await gate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        Interlocked.Add(ref examined, await ScanAsync(unit, target, predicate, needsModel, Emit, Fail, token).ConfigureAwait(false));
                    }
                    finally
                    {
                        gate.Release();
                    }
                });

                try
                {
                    await Task.WhenAll(work).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Either the user stopped the run or the result cap tripped; partial results stand.
                }
            }

            Flush(true);
        }

        watch.Stop();
        outcome.Examined = examined;
        outcome.Matched = Math.Min(matched, maxResults);
        outcome.Errors = errors;
        outcome.FirstError = firstError;
        outcome.Elapsed = watch.Elapsed;
        return outcome;
    }

    private static async Task<int> ScanAsync(
        ScopeUnit unit,
        TargetKind target,
        Delegate predicate,
        bool needsModel,
        Action<QueryHit> emit,
        Action<Exception> fail,
        CancellationToken cancellationToken)
    {
        var document = unit.Document;
        if (!document.SupportsSyntaxTree) return 0;

        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null) return 0;

        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var scopeRoot = unit.Restriction is TextSpan span ? root.FindNode(span, getInnermostNodeForTie: false) : root;

        SemanticModel model = null;
        if (needsModel && document.SupportsSemanticModel)
            model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);

        var local = 0;
        switch (target)
        {
            case TargetKind.SyntaxNode:
                ScanNodes(scopeRoot, model, document, text, (NodeMatch)predicate, emit, fail, ref local, cancellationToken);
                break;
            case TargetKind.SyntaxToken:
                ScanTokens(scopeRoot, model, document, text, (TokenMatch)predicate, emit, fail, ref local, cancellationToken);
                break;
            case TargetKind.Operation:
                if (model != null) ScanOperations(scopeRoot, model, document, text, (OperationMatch)predicate, emit, fail, ref local, cancellationToken);
                break;
        }

        return local;
    }

    private static void ScanNodes(
        SyntaxNode scopeRoot, SemanticModel model, Document document, SourceText text,
        NodeMatch match, Action<QueryHit> emit, Action<Exception> fail, ref int examined, CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var node in scopeRoot.DescendantNodesAndSelf())
        {
            cancellationToken.ThrowIfCancellationRequested();
            count++;

            bool hit;
            try { hit = match(node, model, document); }
            catch (Exception ex) { fail(ex); continue; }

            if (hit) emit(QueryHit.Create(document, text, node.Span, node.Kind().ToString()));
        }

        examined += count;
    }

    private static void ScanTokens(
        SyntaxNode scopeRoot, SemanticModel model, Document document, SourceText text,
        TokenMatch match, Action<QueryHit> emit, Action<Exception> fail, ref int examined, CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var token in scopeRoot.DescendantTokens())
        {
            cancellationToken.ThrowIfCancellationRequested();
            count++;

            bool hit;
            try { hit = match(token, model, document); }
            catch (Exception ex) { fail(ex); continue; }

            if (hit) emit(QueryHit.Create(document, text, token.Span, token.Kind().ToString()));
        }

        examined += count;
    }

    private static void ScanOperations(
        SyntaxNode scopeRoot, SemanticModel model, Document document, SourceText text,
        OperationMatch match, Action<QueryHit> emit, Action<Exception> fail, ref int examined, CancellationToken cancellationToken)
    {
        var count = 0;
        var stack = new Stack<IOperation>();

        // Walk syntax only until a node owns an operation, then switch to the operation tree:
        // GetOperation on every node would re-enter binding for subtrees already covered.
        foreach (var node in scopeRoot.DescendantNodesAndSelf(descendIntoChildren: candidate => model.GetOperation(candidate, cancellationToken) is null))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var root = model.GetOperation(node, cancellationToken);
            if (root is null) continue;

            stack.Push(root);
            while (stack.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var operation = stack.Pop();
                count++;

                foreach (var child in operation.ChildOperations) stack.Push(child);

                bool hit;
                try { hit = match(operation, model, document); }
                catch (Exception ex) { fail(ex); continue; }

                if (hit) emit(QueryHit.Create(document, text, operation.Syntax.Span, operation.Kind.ToString()));
            }
        }

        examined += count;
    }
}
