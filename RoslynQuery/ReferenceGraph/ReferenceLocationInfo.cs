using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoslynQuery.ReferenceGraph;

/// <summary>One occurrence behind a graph node, tagged with how it uses its target.</summary>
internal readonly struct ReferenceLocationInfo
{
    public ReferenceLocationInfo(DocumentId documentId, TextSpan span, ReferenceUsageKind kind)
    {
        DocumentId = documentId;
        Span = span;
        Kind = kind;
    }

    public DocumentId DocumentId { get; }
    public TextSpan Span { get; }
    public ReferenceUsageKind Kind { get; }
}
