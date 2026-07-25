using System;
using System.IO;
using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoslynQuery.Query;

/// <summary>
/// A single match. Deliberately holds no <see cref="SyntaxNode"/>/<see cref="IOperation"/>: those
/// root their whole tree and compilation, and a solution-wide run would pin every one of them.
/// </summary>
internal sealed class QueryHit
{
    private static readonly Regex WhitespaceRun = new Regex(@"\s+", RegexOptions.Compiled);
    private const int PreviewLength = 160;

    public DocumentId DocumentId { get; private set; }
    public string FilePath { get; private set; }
    public string FileName { get; private set; }
    public TextSpan Span { get; private set; }
    public int Line { get; private set; }
    public int Column { get; private set; }
    public int EndLine { get; private set; }
    public int EndColumn { get; private set; }
    public string Kind { get; private set; }
    public string Preview { get; private set; }

    public string Location => $"{FileName} ({Line + 1},{Column + 1})";

    public static QueryHit Create(Document document, SourceText text, TextSpan span, string kind)
    {
        var lines = text.Lines.GetLinePositionSpan(span);

        return new QueryHit
        {
            DocumentId = document.Id,
            FilePath = document.FilePath,
            FileName = string.IsNullOrEmpty(document.FilePath) ? document.Name : Path.GetFileName(document.FilePath),
            Span = span,
            Line = lines.Start.Line,
            Column = lines.Start.Character,
            EndLine = lines.End.Line,
            EndColumn = lines.End.Character,
            Kind = kind,
            Preview = BuildPreview(text, span)
        };
    }

    private static string BuildPreview(SourceText text, TextSpan span)
    {
        var clamped = span.Length <= PreviewLength * 2 ? span : new TextSpan(span.Start, Math.Min(PreviewLength * 2, text.Length - span.Start));
        var raw = WhitespaceRun.Replace(text.ToString(clamped), " ").Trim();
        return raw.Length > PreviewLength ? raw.Substring(0, PreviewLength - 3) + "..." : raw;
    }
}
