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
        var outcome = new QueryOutcome();
        var watch = Stopwatch.StartNew();
        var needsModel = target == TargetKind.Operation || MentionsModel.IsMatch(expression ?? string.Empty);

        var pending = new List<QueryHit>(BatchSize);
        // A predicate may report a different node/token/operation than the one it matched on (see
        // TryClassifyResult) - several distinct matches can then legitimately point at the same
        // result location (e.g. "every await in this method" all reporting the containing method),
        // and without this they would show up as that many duplicate rows for one location.
        var seen = new HashSet<(DocumentId DocumentId, TextSpan Span, string Kind)>();
        var sync = new object();
        var examined = 0;
        var matched = 0;
        var errors = 0;
        var skipped = 0;
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
                        batch = [.. pending];
                        pending.Clear();
                    }
                }

                if (batch != null) onBatch(batch);
            }

            void Emit(QueryHit hit)
            {
                lock (sync)
                {
                    if (!seen.Add((hit.DocumentId, hit.Span, hit.Kind)))
                        return;
                }

                if (Interlocked.Increment(ref matched) > maxResults)
                {
                    outcome.Truncated = true;
                    cap.Cancel();
                    return;
                }

                lock (sync) pending.Add(hit);
                Flush(false);
            }

            void Skip() => Interlocked.Increment(ref skipped);

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
                        Interlocked.Add(ref examined, await ScanAsync(unit, target, predicate, needsModel, Emit, Fail, Skip, token).ConfigureAwait(false));
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
        outcome.Documents = units.Count - skipped;
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
        Action skip,
        CancellationToken cancellationToken)
    {
        var document = unit.Document;
        if (!document.SupportsSyntaxTree) return 0;

        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null) return 0;

        // The name and path test already ran in ScopeResolver. This is the half that needs the
        // tree, and it costs nothing here because the root had to be parsed anyway.
        if (unit.FilterGenerated && GeneratedCode.IsGeneratedTree(root))
        {
            skip();
            return 0;
        }

        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var scopeRoot = unit.Restriction is TextSpan span ? root.FindNode(span, getInnermostNodeForTie: false) : root;

        SemanticModel model = null;
        if (needsModel && document.SupportsSemanticModel)
            model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);

        switch (target)
        {
            case TargetKind.SyntaxNode:
                return await ScanNodesAsync(scopeRoot, model, document, text, (NodeMatch)predicate, emit, fail, cancellationToken).ConfigureAwait(false);
            case TargetKind.SyntaxToken:
                return await ScanTokensAsync(scopeRoot, model, document, text, (TokenMatch)predicate, emit, fail, cancellationToken).ConfigureAwait(false);
            case TargetKind.Operation:
                if (model is null) return 0;
                return await ScanOperationsAsync(scopeRoot, model, document, text, (OperationMatch)predicate, emit, fail, cancellationToken).ConfigureAwait(false);
            default:
                return 0;
        }
    }

    // The scan methods return their count rather than taking `ref int examined`: an async method
    // cannot have a ref parameter (CS1988).
    private static async Task<int> ScanNodesAsync(
        SyntaxNode scopeRoot, SemanticModel model, Document document, SourceText text,
        NodeMatch match, Action<QueryHit> emit, Action<Exception> fail, CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var node in scopeRoot.DescendantNodesAndSelf())
        {
            cancellationToken.ThrowIfCancellationRequested();
            count++;

            bool hit; TextSpan span; string kind;
            try
            {
                var result = await match(node, model, document).ConfigureAwait(false);
                hit = TryClassifyResult(TargetKind.SyntaxNode, result, node.SyntaxTree, node.Span, node.Kind().ToString(), out span, out kind);
            }
            catch (Exception ex) { fail(ex); continue; }

            if (hit) emit(QueryHit.Create(document, text, span, kind, TargetKind.SyntaxNode));
        }

        return count;
    }

    private static async Task<int> ScanTokensAsync(
        SyntaxNode scopeRoot, SemanticModel model, Document document, SourceText text,
        TokenMatch match, Action<QueryHit> emit, Action<Exception> fail, CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var token in scopeRoot.DescendantTokens())
        {
            cancellationToken.ThrowIfCancellationRequested();
            count++;

            bool hit; TextSpan span; string kind;
            try
            {
                var result = await match(token, model, document).ConfigureAwait(false);
                hit = TryClassifyResult(TargetKind.SyntaxToken, result, token.SyntaxTree, token.Span, token.Kind().ToString(), out span, out kind);
            }
            catch (Exception ex) { fail(ex); continue; }

            if (hit) emit(QueryHit.Create(document, text, span, kind, TargetKind.SyntaxToken));
        }

        return count;
    }

    private static async Task<int> ScanOperationsAsync(
        SyntaxNode scopeRoot, SemanticModel model, Document document, SourceText text,
        OperationMatch match, Action<QueryHit> emit, Action<Exception> fail, CancellationToken cancellationToken)
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

                bool hit; TextSpan span; string kind;
                try
                {
                    var result = await match(operation, model, document).ConfigureAwait(false);
                    hit = TryClassifyResult(TargetKind.Operation, result, operation.Syntax.SyntaxTree, operation.Syntax.Span, operation.Kind.ToString(), out span, out kind);
                }
                catch (Exception ex) { fail(ex); continue; }

                if (hit) emit(QueryHit.Create(document, text, span, kind, TargetKind.Operation));
            }
        }

        return count;
    }

    /// <summary>
    /// Interprets a predicate's <c>object</c> return: <see langword="null"/> acts as
    /// <see langword="false"/>; a <see langword="bool"/> is the match as before; a
    /// SyntaxNode/SyntaxToken/IOperation matching <paramref name="target"/> lets the predicate pick
    /// a different result location than the parameter it was handed - a Where+Select in one.
    /// </summary>
    /// <remarks>
    /// A returned SyntaxNode/SyntaxToken/IOperation must belong to <paramref name="tree"/> - the
    /// tree actually being searched - or this throws instead of emitting a hit. Without that check a
    /// predicate could hand back a node built with SyntaxFactory (or lifted from an unrelated
    /// document), and QueryHit would record a bogus span/kind: navigation would jump nowhere
    /// meaningful, and if the hit later reached Replace, ReplaceEngine's node/token re-resolution
    /// (span+kind lookup against the live tree) would silently misfire or "helpfully" match some
    /// unrelated node that happens to share that span and kind.
    /// </remarks>
    private static bool TryClassifyResult(TargetKind target, object result, SyntaxTree tree, TextSpan defaultSpan, string defaultKind, out TextSpan span, out string kind)
    {
        switch (result)
        {
            case null:
                span = default;
                kind = null;
                return false;
            case bool matched:
                span = defaultSpan;
                kind = defaultKind;
                return matched;
            case SyntaxNode node when target == TargetKind.SyntaxNode:
                if (node.SyntaxTree != tree)
                    throw new InvalidOperationException("The query returned a SyntaxNode that is not part of the tree being searched - only a node obtained from 'n' (or one of its descendants) can be returned as a result.");
                span = node.Span;
                kind = node.Kind().ToString();
                return true;
            case SyntaxToken token when target == TargetKind.SyntaxToken:
                if (token.SyntaxTree != tree)
                    throw new InvalidOperationException("The query returned a SyntaxToken that is not part of the tree being searched - only a token obtained from 't' (or a sibling/descendant token) can be returned as a result.");
                span = token.Span;
                kind = token.Kind().ToString();
                return true;
            case IOperation operation when target == TargetKind.Operation:
                if (operation.Syntax.SyntaxTree != tree)
                    throw new InvalidOperationException("The query returned an IOperation that is not part of the tree being searched - only an operation obtained from 'op' (or one of its descendants) can be returned as a result.");
                span = operation.Syntax.Span;
                kind = operation.Kind.ToString();
                return true;
            default:
                throw new InvalidOperationException($"The query returned {result.GetType().Name}, which is not valid for a {target} search; expected bool, {PredicateTemplate.ParameterType(target)}, or null.");
        }
    }
}
