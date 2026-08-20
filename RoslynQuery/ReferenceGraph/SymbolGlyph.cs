using Microsoft.CodeAnalysis;

namespace RoslynQuery.ReferenceGraph;

/// <summary>
/// The icon a node shows. Computed once when the node is built so the WPF converter stays a pure
/// function over an enum and the node never has to hold the symbol it came from.
/// </summary>
internal enum SymbolGlyph
{
    Unknown,
    Method,
    Constructor,
    Property,
    Field,
    Event,
    Constant,
    EnumMember,
    Class,
    Structure,
    Interface,
    Enumeration,
    Delegate,
    IncomingBranch,
    OutgoingBranch,

    /// <summary>The synthetic row grouping one node's individual occurrences, and one such occurrence.</summary>
    Locations,
    Location
}

internal static class SymbolGlyphs
{
    public static SymbolGlyph For(ISymbol symbol)
    {
        switch (symbol)
        {
            case IMethodSymbol method:
                return method.MethodKind == MethodKind.Constructor || method.MethodKind == MethodKind.StaticConstructor
                    ? SymbolGlyph.Constructor
                    : SymbolGlyph.Method;

            case IPropertySymbol _:
                return SymbolGlyph.Property;

            case IEventSymbol _:
                return SymbolGlyph.Event;

            case IFieldSymbol field:
                if (field.ContainingType?.TypeKind == TypeKind.Enum) return SymbolGlyph.EnumMember;
                return field.IsConst ? SymbolGlyph.Constant : SymbolGlyph.Field;

            case INamedTypeSymbol type:
                switch (type.TypeKind)
                {
                    case TypeKind.Class: return SymbolGlyph.Class;
                    case TypeKind.Struct: return SymbolGlyph.Structure;
                    case TypeKind.Interface: return SymbolGlyph.Interface;
                    case TypeKind.Enum: return SymbolGlyph.Enumeration;
                    case TypeKind.Delegate: return SymbolGlyph.Delegate;
                    default: return SymbolGlyph.Unknown;
                }

            default:
                return SymbolGlyph.Unknown;
        }
    }
}
