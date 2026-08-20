using System.IO;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoslynQuery.ReferenceGraph;

/// <summary>One occurrence behind a graph node, tagged with how it uses its target.</summary>
internal readonly struct ReferenceLocationInfo
{
    public ReferenceLocationInfo(DocumentId documentId, TextSpan span, ReferenceUsageKind kind)
        : this(documentId, null, span, 0, 0, kind)
    {
    }

    public ReferenceLocationInfo(DocumentId documentId, string filePath, TextSpan span, int line, int column, ReferenceUsageKind kind)
    {
        DocumentId = documentId;
        FilePath = filePath;
        Span = span;
        Line = line;
        Column = column;
        Kind = kind;
    }

    public DocumentId DocumentId { get; }

    /// <summary>
    /// The physical file, which is what identifies an occurrence. A file compiled into several
    /// projects - a linked file, or any multi-targeted project - has one
    /// <see cref="DocumentId"/> per project but is still the one place in the source.
    /// </summary>
    public string FilePath { get; }

    public TextSpan Span { get; }
    public int Line { get; }
    public int Column { get; }
    public ReferenceUsageKind Kind { get; }

    public string FileName => string.IsNullOrEmpty(FilePath) ? string.Empty : Path.GetFileName(FilePath);

    public string Display => $"{FileName} ({Line + 1},{Column + 1})";

    public static ReferenceLocationInfo Create(Document document, SourceText text, TextSpan span, ReferenceUsageKind kind)
    {
        var start = text.Lines.GetLinePosition(span.Start);

        return new ReferenceLocationInfo(
            document.Id,
            document.FilePath ?? document.Name,
            span,
            start.Line,
            start.Character,
            kind);
    }

    public ReferenceLocationInfo WithKind(ReferenceUsageKind kind) =>
        new ReferenceLocationInfo(DocumentId, FilePath, Span, Line, Column, kind);
}
