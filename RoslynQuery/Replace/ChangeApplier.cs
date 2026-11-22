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
        var plan = await PlanAsync(workspace, ranAgainst, items, cancellationToken).ConfigureAwait(true);
        return await CommitAsync(workspace, plan, enrollDocument, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Writes a plan to the workspace. Refuses (Applied 0, a warning) when any planned document was edited since the plan
    /// read its text, so nothing typed meanwhile is overwritten.
    /// </summary>
    public static async Task<ApplyOutcome> CommitAsync(
        Workspace workspace, ApplyPlan plan, System.Action<DocumentId> enrollDocument, CancellationToken cancellationToken)
    {
        var outcome = plan.Outcome;
        if (outcome.Applied == 0)
            return outcome;

        var solution = plan.Solution;
        var changedDocuments = plan.ChangedDocuments;
        var warnings = (List<string>)outcome.Warnings;

        var finalTexts = new List<(DocumentId Id, SourceText Text)>(changedDocuments.Count);
        foreach (var id in changedDocuments)
            finalTexts.Add((id, await solution.GetDocument(id).GetTextAsync(cancellationToken).ConfigureAwait(true)));

        // Rebased onto the freshest CurrentSolution: background work can advance it during the awaits
        // above, and TryApplyChanges silently rejects a solution diffed against a stale baseline.
        Solution latest;
        var stale = false;
        for (var attempt = 0; ; attempt++)
        {
            latest = workspace.CurrentSolution;
            foreach (var (id, _) in finalTexts)
            {
                var text = await latest.GetDocument(id).GetTextAsync(cancellationToken).ConfigureAwait(true);
                if (plan.BaseTexts.TryGetValue(id, out var planned) && !text.ContentEquals(planned)) stale = true;
            }

            if (stale || ReferenceEquals(latest, workspace.CurrentSolution) || attempt >= 3) break;
        }

        if (stale)
        {
            outcome.Skipped += outcome.Applied;
            outcome.Applied = 0;
            warnings.Add("A file was edited while the replacement was being applied. Nothing was written; run it again.");
            return outcome;
        }

        if (enrollDocument != null)
        {
            foreach (var id in changedDocuments)
                enrollDocument(id);
        }

        foreach (var (id, text) in finalTexts)
            latest = latest.WithDocumentText(id, text);

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

    /// <summary>The result of <see cref="PlanAsync"/>: the edited solution, not yet written anywhere.</summary>
    internal sealed class ApplyPlan
    {
        public Solution Solution { get; set; }
        public IReadOnlyList<DocumentId> ChangedDocuments { get; set; }

        /// <summary>The text each changed document had when it was planned.</summary>
        public IReadOnlyDictionary<DocumentId, SourceText> BaseTexts { get; set; } = new Dictionary<DocumentId, SourceText>();

        /// <summary>Applied counts what the plan would write; Warnings is always a <see cref="List{T}"/>.</summary>
        public ApplyOutcome Outcome { get; set; }
    }

    /// <summary>
    /// Everything <see cref="ApplyAsync(Workspace, Solution, IReadOnlyList{ReplacementItem}, System.Action{DocumentId}, CancellationToken)"/>
    /// does short of touching the workspace: remaps spans, splices the replacements in and formats them. The
    /// headless CLI uses it on its own for a dry run.
    /// </summary>
    internal static async Task<ApplyPlan> PlanAsync(
        Workspace workspace, Solution ranAgainst, IReadOnlyList<ReplacementItem> items, CancellationToken cancellationToken)
    {
        var outcome = new ApplyOutcome();
        var warnings = new List<string>();
        var included = items.Where(i => i.Included && i.After != null).ToList();
        if (included.Count == 0)
        {
            outcome.Warnings = warnings;
            return new ApplyPlan { Solution = workspace.CurrentSolution, ChangedDocuments = new List<DocumentId>(), Outcome = outcome };
        }

        var current = workspace.CurrentSolution;
        var solution = current;
        var changedDocuments = new List<DocumentId>();
        var baseTexts = new Dictionary<DocumentId, SourceText>();

        // Per physical file: linked copies of one file (one per target framework) share a single text, so their edits must merge.
        foreach (var group in included.GroupBy(i => i.Hit.FilePath ?? i.Hit.DocumentId.ToString()))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var currentDoc = group.Select(i => current.GetDocument(i.Hit.DocumentId)).FirstOrDefault(d => d != null);
            if (currentDoc is null)
            {
                outcome.Skipped += group.Count();
                warnings.Add("A matched document is no longer part of the solution; re-run Search.");
                continue;
            }

            // ConfigureAwait(true) throughout: enrolment and TryApplyChanges below touch live text
            // buffers and the shell's undo stack, both of which are main-thread only.
            var currentText = await currentDoc.GetTextAsync(cancellationToken).ConfigureAwait(true);

            // Remap each hit's span from the search snapshot to the live document. When nothing has
            // edited that document since Search ran, original and current are the same Document
            // instance and the recorded span is already correct.
            var remapped = new List<(ReplacementItem Item, TextSpan Span)>(group.Count());
            foreach (var item in group)
            {
                var itemDoc = current.GetDocument(item.Hit.DocumentId);
                if (itemDoc is null)
                {
                    outcome.Skipped++;
                    warnings.Add("A matched document is no longer part of the solution; re-run Search.");
                    continue;
                }

                var span = item.Hit.Span;
                var originalDoc = ranAgainst?.GetDocument(item.Hit.DocumentId);
                if (originalDoc != null && originalDoc != itemDoc)
                    span = await SpanMapper.MapForwardAsync(originalDoc, itemDoc, span, cancellationToken).ConfigureAwait(true);

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
            var newSolution = solution.WithDocumentText(currentDoc.Id, newText);

            // A formatting failure must never turn an otherwise-correct apply into a failed one.
            try
            {
                var formatted = await Formatter.FormatAsync(newSolution.GetDocument(currentDoc.Id), newSpans, cancellationToken: cancellationToken)
                    .ConfigureAwait(true);
                newSolution = newSolution.WithDocumentText(currentDoc.Id, await formatted.GetTextAsync(cancellationToken).ConfigureAwait(true));
            }
            catch
            {
                // Keep newSolution's unformatted text.
            }

            solution = newSolution;
            changedDocuments.Add(currentDoc.Id);
            baseTexts[currentDoc.Id] = currentText;
        }

        outcome.Warnings = warnings;
        return new ApplyPlan { Solution = solution, ChangedDocuments = changedDocuments, BaseTexts = baseTexts, Outcome = outcome };
    }
}
