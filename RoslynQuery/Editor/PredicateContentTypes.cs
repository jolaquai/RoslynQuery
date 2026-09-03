using System.ComponentModel.Composition;

using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Utilities;

using RoslynQuery.Mcp.Contracts;

namespace RoslynQuery.Editor;

internal static class PredicateContentTypes
{
    public const string Name = "RoslynQueryPredicate";

    // A private content type keeps our completion source and classifier off every real C# buffer.
#pragma warning disable 649
    [Export]
    [Name(Name)]
    [BaseDefinition("code")]
    internal static ContentTypeDefinition Definition;
#pragma warning restore 649
}

/// <summary>The completion applicable-to span, in exactly one place - the source and input drifting out of sync here is what previously made a commit eat the text in front of the word.</summary>
internal static class PredicateWord
{
    public static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    public static Span At(ITextSnapshot snapshot, int position)
    {
        var start = position;
        while (start > 0 && IsIdentifierChar(snapshot[start - 1])) start--;
        return Span.FromBounds(start, position);
    }
}

/// <summary>Per-buffer state the MEF parts need but cannot be handed through a constructor.</summary>
internal sealed class PredicateBufferContext
{
    public TargetKind Target { get; set; } = TargetKind.SyntaxNode;

    public static TargetKind GetTarget(ITextBuffer buffer) =>
        buffer.Properties.TryGetProperty(typeof(PredicateBufferContext), out PredicateBufferContext context)
            ? context.Target
            : TargetKind.SyntaxNode;

    public static void Attach(ITextBuffer buffer, PredicateBufferContext context) =>
        buffer.Properties[typeof(PredicateBufferContext)] = context;
}
