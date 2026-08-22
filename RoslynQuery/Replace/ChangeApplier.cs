using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
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
    public static async Task<ApplyOutcome> ApplyAsync(
        Workspace workspace, Solution ranAgainst, IReadOnlyList<ReplacementItem> items, CancellationToken cancellationToken)
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

            var currentText = await currentDoc.GetTextAsync(cancellationToken).ConfigureAwait(false);
            var originalDoc = ranAgainst?.GetDocument(group.Key);

            // Remap each hit's span from the search snapshot to the live document. When nothing has
            // edited that document since Search ran, original and current are the same Document
            // instance and the recorded span is already correct.
            var remapped = new List<(ReplacementItem Item, TextSpan Span)>(group.Count());
            foreach (var item in group)
            {
                var span = item.Hit.Span;
                if (originalDoc != null && originalDoc != currentDoc)
                    span = await SpanMapper.MapForwardAsync(originalDoc, currentDoc, span, cancellationToken).ConfigureAwait(false);

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

            var changes = new List<TextChange>(remapped.Count);
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
                previous = span;
                outcome.Applied++;
            }

            if (changes.Count == 0)
                continue;

            var newText = currentText.WithChanges(changes);
            solution = solution.WithDocumentText(group.Key, newText);
        }

        outcome.Warnings = warnings;
        if (outcome.Applied == 0)
            return outcome;

        if (!workspace.TryApplyChanges(solution))
        {
            outcome.Skipped += outcome.Applied;
            outcome.Applied = 0;
            warnings.Add("The workspace rejected the change set.");
        }

        return outcome;
    }
}
