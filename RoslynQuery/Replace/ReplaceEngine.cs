using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

using RoslynQuery.Query;

namespace RoslynQuery.Replace;

/// <summary>Turns a set of <see cref="QueryHit"/>s from a completed Search run into previewed <see cref="ReplacementItem"/>s.</summary>
internal static class ReplaceEngine
{
    public static async Task<IReadOnlyList<ReplacementItem>> GenerateAsync(
        Solution ranAgainst, IReadOnlyList<QueryHit> hits, TargetKind target, Delegate replace, CancellationToken cancellationToken)
    {
        var items = new ReplacementItem[hits.Count];

        foreach (var group in hits.Select((hit, index) => (hit, index)).GroupBy(entry => entry.hit.DocumentId))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var document = ranAgainst?.GetDocument(group.Key);
            if (document is null)
            {
                foreach (var entry in group)
                    items[entry.index] = Skip(entry.hit, "The search snapshot is gone; re-run Search.");
                continue;
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root is null)
            {
                foreach (var entry in group)
                    items[entry.index] = Skip(entry.hit, "This document no longer has a syntax tree.");
                continue;
            }

            SemanticModel model = document.SupportsSemanticModel
                ? await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false)
                : null;

            // Span alone can't tell same-span wrapper nesting apart (an ExpressionStatement around its
            // one expression, say), so Kind resolves the tie.
            var nodeIndex = target == TargetKind.SyntaxNode ? BuildNodeIndex(root) : null;

            using (var gate = new SemaphoreSlim(Environment.ProcessorCount))
            {
                var work = group.Select(async entry =>
                {
                    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        items[entry.index] = await GenerateOneAsync(entry.hit, root, nodeIndex, model, document, target, replace, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        gate.Release();
                    }
                });

                await Task.WhenAll(work).ConfigureAwait(false);
            }
        }

        return items;
    }

    private static Dictionary<(TextSpan Span, string Kind), SyntaxNode> BuildNodeIndex(SyntaxNode root)
    {
        var index = new Dictionary<(TextSpan, string), SyntaxNode>();
        foreach (var node in root.DescendantNodesAndSelf())
            index[(node.Span, node.Kind().ToString())] = node;
        return index;
    }

    private static async Task<ReplacementItem> GenerateOneAsync(
        QueryHit hit, SyntaxNode root, Dictionary<(TextSpan Span, string Kind), SyntaxNode> nodeIndex, SemanticModel model,
        Document document, TargetKind target, Delegate replace, CancellationToken cancellationToken)
    {
        object result;
        try
        {
            if (target == TargetKind.SyntaxNode)
            {
                if (!nodeIndex.TryGetValue((hit.Span, hit.Kind), out var node))
                    return Skip(hit, "This match no longer exists at its recorded location.");

                result = await ((NodeReplace)replace)(node, model, document).ConfigureAwait(false);
            }
            else
            {
                var token = root.FindToken(hit.Span.Start);
                if (token.Span != hit.Span || token.Kind().ToString() != hit.Kind)
                    return Skip(hit, "This match no longer exists at its recorded location.");

                result = await ((TokenReplace)replace)(token, model, document).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            return new ReplacementItem { Hit = hit, Before = hit.Preview, After = null, Included = false, Warning = ex.GetType().Name + ": " + ex.Message };
        }

        return Classify(hit, result);
    }

    private static ReplacementItem Classify(QueryHit hit, object result)
    {
        switch (result)
        {
            case null:
                return Skip(hit, "The replacement returned null for this match.");
            case string text:
                return new ReplacementItem { Hit = hit, Before = hit.Preview, After = text };
            case SyntaxNode node:
                // NormalizeWhitespace, not Formatter: Formatter needs a live document/options and a
                // span to reformat post-apply, which is ChangeApplier's job, not preview generation's.
                return new ReplacementItem { Hit = hit, Before = hit.Preview, After = node.NormalizeWhitespace().ToFullString() };
            case SyntaxToken token:
                // hit.Span is the token's Span (trivia excluded, per QueryEngine.ScanTokensAsync), so
                // the replacement must exclude trivia too or it would duplicate whatever surrounds it.
                return new ReplacementItem { Hit = hit, Before = hit.Preview, After = token.Text };
            default:
                return Skip(hit, "The replacement returned " + result.GetType().Name + "; expected string, SyntaxNode, or SyntaxToken.");
        }
    }

    private static ReplacementItem Skip(QueryHit hit, string warning) =>
        new ReplacementItem { Hit = hit, Before = hit.Preview, After = null, Included = false, Warning = warning };

    /// <summary>
    /// Unchecks (and flags) any item whose span overlaps a still-included item earlier in the same
    /// document: applying both would splice one replacement's text into the middle of another.
    /// </summary>
    public static void MarkConflicts(IReadOnlyList<ReplacementItem> items)
    {
        foreach (var group in items.Where(i => i.Included).GroupBy(i => i.Hit.DocumentId))
        {
            ReplacementItem previous = null;
            foreach (var item in group.OrderBy(i => i.Hit.Span.Start))
            {
                if (previous != null && item.Hit.Span.OverlapsWith(previous.Hit.Span))
                {
                    item.Included = false;
                    item.Warning = "Overlaps another included replacement in this file.";
                    continue;
                }

                previous = item;
            }
        }
    }
}
