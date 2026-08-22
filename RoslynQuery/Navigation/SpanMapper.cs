using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoslynQuery.Navigation;

internal sealed class NavigationTarget
{
    public string FilePath { get; set; }
    public int Line { get; set; }
    public int Column { get; set; }
    public int EndLine { get; set; }
    public int EndColumn { get; set; }
    public bool Remapped { get; set; }
}

internal static class SpanMapper
{
    /// <summary>
    /// Spans are recorded against the snapshot the query ran on. Any edit above a match shifts it,
    /// so the span is replayed through the diff before it is turned into a caret position.
    /// </summary>
    public static async Task<NavigationTarget> ResolveAsync(
        Solution ranAgainst, Solution current, DocumentId documentId, TextSpan span, CancellationToken cancellationToken)
    {
        var document = current?.GetDocument(documentId);
        if (document is null) return null;

        var mapped = span;
        var remapped = false;

        var original = ranAgainst?.GetDocument(documentId);
        if (original != null && original != document)
        {
            mapped = await MapForwardAsync(original, document, span, cancellationToken).ConfigureAwait(false);
            remapped = mapped != span;
        }

        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var clamped = Clamp(mapped, text.Length);
        var lines = text.Lines.GetLinePositionSpan(clamped);

        return new NavigationTarget
        {
            FilePath = document.FilePath,
            Line = lines.Start.Line,
            Column = lines.Start.Character,
            EndLine = lines.End.Line,
            EndColumn = lines.End.Character,
            Remapped = remapped
        };
    }

    /// <summary>Also used directly by <see cref="RoslynQuery.Replace.ChangeApplier"/>, which needs the mapped span itself rather than a caret position.</summary>
    internal static async Task<TextSpan> MapForwardAsync(Document original, Document current, TextSpan span, CancellationToken cancellationToken)
    {
        var start = span.Start;
        var end = span.End;

        // Change spans are in the original document's coordinates, so applying them in order and
        // accumulating the delta is enough; no tracking spans and no open buffer required.
        foreach (var change in (await current.GetTextChangesAsync(original, cancellationToken).ConfigureAwait(false)).OrderBy(c => c.Span.Start))
        {
            if (change.Span.Start > end) break;

            var delta = (change.NewText?.Length ?? 0) - change.Span.Length;
            if (change.Span.End <= start)
            {
                start += delta;
                end += delta;
            }
            else
            {
                // The edit lands inside the match: keep the start and let the end absorb the delta.
                end = Math.Max(start, end + delta);
            }
        }

        return TextSpan.FromBounds(Math.Max(0, start), Math.Max(Math.Max(0, start), end));
    }

    private static TextSpan Clamp(TextSpan span, int length)
    {
        var start = Math.Min(Math.Max(0, span.Start), length);
        var end = Math.Min(Math.Max(start, span.End), length);
        return TextSpan.FromBounds(start, end);
    }
}
