using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;

using RoslynQuery.Navigation;

namespace RoslynQuery.Replace;

internal sealed class ApplyOutcome
{
    public int Applied { get; set; }
    public int Skipped { get; set; }
    public IReadOnlyList<string> Warnings { get; set; } = System.Array.Empty<string>();
}

/// <summary>
/// Writes accepted <see cref="ReplacementItem"/>s back to the workspace. Takes the base
/// <see cref="Workspace"/> type (not <c>VisualStudioWorkspace</c>) so it can be exercised against an
/// <see cref="AdhocWorkspace"/> in tests; the tool window passes its real
/// <c>VisualStudioWorkspace</c>, which is one.
/// </summary>
internal static class ChangeApplier
{
    public static Task<ApplyOutcome> ApplyAsync(
        Workspace workspace, Solution ranAgainst, IReadOnlyList<ReplacementItem> items, CancellationToken cancellationToken)
        => ApplyAsync(workspace, ranAgainst, items, null, cancellationToken);

    /// <param name="enrollDocument">
    /// Called on the UI thread for every document about to change, immediately before the single
    /// <see cref="Workspace.TryApplyChanges"/>. The tool window uses it to pull each file into an
    /// open linked undo transaction; null in tests.
    /// </param>
    public static async Task<ApplyOutcome> ApplyAsync(
        Workspace workspace, Solution ranAgainst, IReadOnlyList<ReplacementItem> items,
        System.Action<DocumentId> enrollDocument, CancellationToken cancellationToken)
    {
        var outcome = new ApplyOutcome();
        var warnings = new List<string>();
        var included = items.Where(i => i.Included && i.After != null).ToList();
        if (included.Count == 0)
        {
            outcome.Warnings = warnings;
            return outcome;
        }

        var current = workspace.CurrentSolution;
        var solution = current;
        var changedDocuments = new List<DocumentId>();

        foreach (var group in included.GroupBy(i => i.Hit.DocumentId))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var currentDoc = current.GetDocument(group.Key);
            if (currentDoc is null)
            {
                outcome.Skipped += group.Count();
                warnings.Add("A matched document is no longer part of the solution; re-run Search.");
                continue;
            }

            // ConfigureAwait(true) throughout: enrolment and TryApplyChanges below touch live text
            // buffers and the shell's undo stack, both of which are main-thread only.
            var currentText = await currentDoc.GetTextAsync(cancellationToken).ConfigureAwait(true);
            var originalDoc = ranAgainst?.GetDocument(group.Key);

            // Remap each hit's span from the search snapshot to the live document. When nothing has
            // edited that document since Search ran, original and current are the same Document
            // instance and the recorded span is already correct.
            var remapped = new List<(ReplacementItem Item, TextSpan Span)>(group.Count());
            foreach (var item in group)
            {
                var span = item.Hit.Span;
                if (originalDoc != null && originalDoc != currentDoc)
                    span = await SpanMapper.MapForwardAsync(originalDoc, currentDoc, span, cancellationToken).ConfigureAwait(true);

                if (span.Start < 0 || span.End > currentText.Length)
                {
                    outcome.Skipped++;
                    warnings.Add($"{item.Hit.FileName}: match is stale, re-run Search.");
                    continue;
                }

                remapped.Add((item, span));
            }

            // Overlaps can appear post-remap even when ReplaceEngine.MarkConflicts found none in the
            // search snapshot: an earlier edit can widen one span into a later one's territory. This
            // re-checks against the live spans rather than trusting that earlier verdict.
            remapped.Sort((a, b) => a.Span.Start.CompareTo(b.Span.Start));

            // Tracks where each accepted change lands in the merged text: changes earlier in the sort
            // shift every span after them by their own length delta, and Formatter needs those final
            // positions, not the pre-splice ones the spans were recorded at.
            var changes = new List<TextChange>(remapped.Count);
            var newSpans = new List<TextSpan>(remapped.Count);
            var delta = 0;
            TextSpan? previous = null;
            foreach (var (item, span) in remapped)
            {
                if (previous.HasValue && span.OverlapsWith(previous.Value))
                {
                    outcome.Skipped++;
                    warnings.Add($"{item.Hit.FileName}: overlaps another replacement after remapping, skipped.");
                    continue;
                }

                changes.Add(new TextChange(span, item.After));
                newSpans.Add(new TextSpan(span.Start + delta, item.After.Length));
                delta += item.After.Length - span.Length;
                previous = span;
                outcome.Applied++;
            }

            if (changes.Count == 0)
                continue;

            var newText = currentText.WithChanges(changes);
            var newSolution = solution.WithDocumentText(group.Key, newText);

            // Best-effort: raw text splicing (especially a SyntaxNode/SyntaxToken result's own
            // formatting) can leave indentation wrong relative to its new surroundings. A formatting
            // failure must never turn an otherwise-correct apply into a failed one, so fall back to
            // the unformatted (but still correct) text rather than letting this throw.
            try
            {
                var formatted = await Formatter.FormatAsync(newSolution.GetDocument(group.Key), newSpans, cancellationToken: cancellationToken)
                    .ConfigureAwait(true);
                newSolution = newSolution.WithDocumentText(group.Key, await formatted.GetTextAsync(cancellationToken).ConfigureAwait(true));
            }
            catch
            {
                // Keep newSolution's unformatted text.
            }

            solution = newSolution;
            changedDocuments.Add(group.Key);
        }

        outcome.Warnings = warnings;
        if (outcome.Applied == 0)
            return outcome;

        if (enrollDocument != null)
        {
            foreach (var id in changedDocuments)
                enrollDocument(id);
        }

        if (!workspace.TryApplyChanges(solution))
        {
            outcome.Skipped += outcome.Applied;
            outcome.Applied = 0;
            warnings.Add("The workspace rejected the change set.");
        }

        return outcome;
    }
}
