using Microsoft.CodeAnalysis;

namespace RoslynQuery.ReferenceGraph;

/// <summary>One classified run of a row's signature. Not <see cref="SymbolDisplayPart"/>, whose <c>Symbol</c> would pin the compilation.</summary>
internal readonly struct SignaturePart
{
    public SignaturePart(SymbolDisplayPartKind kind, string text)
    {
        Kind = kind;
        Text = text;
    }

    public SymbolDisplayPartKind Kind { get; }
    public string Text { get; }

    public override string ToString() => Text;
}
