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

/// <summary>Writes accepted <see cref="ReplacementItem"/>s back to the workspace.</summary>
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

            // Re-checked against live spans: an earlier edit can widen a span into a later one's
            // territory post-remap even when ReplaceEngine.MarkConflicts found no conflict earlier.
            remapped.Sort((a, b) => a.Span.Start.CompareTo(b.Span.Start));

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

                // Widened to include leading whitespace so Formatter reindents it too.
                var leadingWhitespaceStart = span.Start;
                while (leadingWhitespaceStart > 0 && char.IsWhiteSpace(currentText[leadingWhitespaceStart - 1]))
                    leadingWhitespaceStart--;

                var trailingWhitespaceEnd = span.End;
                while (trailingWhitespaceEnd < currentText.Length && char.IsWhiteSpace(currentText[trailingWhitespaceEnd]))
                    trailingWhitespaceEnd++;

                var afterDelta = delta + (item.After.Length - span.Length);
                newSpans.Add(TextSpan.FromBounds(leadingWhitespaceStart + delta, trailingWhitespaceEnd + afterDelta));

                delta = afterDelta;
                previous = span;
                outcome.Applied++;
            }

            if (changes.Count == 0)
                continue;

            var newText = currentText.WithChanges(changes);
            var newSolution = solution.WithDocumentText(group.Key, newText);

            // A formatting failure must never turn an otherwise-correct apply into a failed one.
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

        // Rebased onto the freshest CurrentSolution: background work can advance it during the awaits
        // above, and TryApplyChanges silently rejects a solution diffed against a stale baseline.
        var latest = workspace.CurrentSolution;
        foreach (var id in changedDocuments)
        {
            var finalText = await solution.GetDocument(id).GetTextAsync(cancellationToken).ConfigureAwait(true);
            latest = latest.WithDocumentText(id, finalText);
        }

        var diagnostics = new List<string>();
        bool applied;
        using (workspace.RegisterWorkspaceFailedHandler(e => diagnostics.Add(e.Diagnostic.Message)))
        {
            applied = workspace.TryApplyChanges(latest);
        }

        if (!applied)
        {
            outcome.Skipped += outcome.Applied;
            outcome.Applied = 0;
            var detail = diagnostics.Count > 0 ? string.Join("; ", diagnostics) : "no diagnostic reported";
            warnings.Add($"The workspace rejected the change set ({detail}).");
        }

        return outcome;
    }
}
