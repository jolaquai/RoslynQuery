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

    public readonly DocumentId DocumentId;
    public readonly string FilePath;
    public readonly string FileName;
    public readonly TextSpan Span;
    public readonly int Line;
    public readonly int Column;
    public readonly int EndLine;
    public readonly int EndColumn;
    public readonly string Kind;
    public readonly string Preview;
    public readonly TargetKind Target;

    private QueryHit(DocumentId documentId, string filePath, string fileName, TextSpan span, int line, int column, int endLine, int endColumn, string kind, string preview, TargetKind target)
    {
        DocumentId = documentId;
        FilePath = filePath;
        FileName = fileName;
        Span = span;
        Line = line;
        Column = column;
        EndLine = endLine;
        EndColumn = endColumn;
        Kind = kind;
        Preview = preview;
        Target = target;
    }

    public string Location => $"{FileName} ({Line + 1},{Column + 1})";

    public static QueryHit Create(Document document, SourceText text, TextSpan span, string kind, TargetKind target)
    {
        var lines = text.Lines.GetLinePositionSpan(span);

        return new QueryHit(document.Id, document.FilePath, string.IsNullOrEmpty(document.FilePath) ? document.Name : Path.GetFileName(document.FilePath), span, lines.Start.Line, lines.Start.Character, lines.End.Line, lines.End.Character, kind, BuildPreview(text, span), target);
    }

    private static string BuildPreview(SourceText text, TextSpan span)
    {
        var clamped = span.Length <= PreviewLength * 2 ? span : new TextSpan(span.Start, Math.Min(PreviewLength * 2, text.Length - span.Start));
        var raw = WhitespaceRun.Replace(text.ToString(clamped), " ").Trim();
        return raw.Length > PreviewLength ? raw.Substring(0, PreviewLength - 3) + "..." : raw;
    }
}
