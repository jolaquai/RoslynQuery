using Microsoft.CodeAnalysis;

namespace RoslynQuery.ReferenceGraph;

/// <summary>The icon a node shows.</summary>
internal enum SymbolGlyph
{
    Unknown,
    Method,
    Constructor,
    Operator,
    LocalFunction,
    Lambda,
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
    Namespace,
    Local,
    Parameter,
    TypeParameter,
    IncomingBranch,
    OutgoingBranch,
    HierarchyBranch,

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
                switch (method.MethodKind)
                {
                    case MethodKind.Constructor:
                    case MethodKind.StaticConstructor:
                        return SymbolGlyph.Constructor;
                    case MethodKind.UserDefinedOperator:
                    case MethodKind.Conversion:
                        return SymbolGlyph.Operator;
                    case MethodKind.LocalFunction:
                        return SymbolGlyph.LocalFunction;
                    case MethodKind.AnonymousFunction:
                        return SymbolGlyph.Lambda;
                    default:
                        return SymbolGlyph.Method;
                }

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

            case INamespaceSymbol _:
                return SymbolGlyph.Namespace;

            case ILocalSymbol _:
                return SymbolGlyph.Local;

            case IParameterSymbol _:
                return SymbolGlyph.Parameter;

            case ITypeParameterSymbol _:
                return SymbolGlyph.TypeParameter;

            default:
                return SymbolGlyph.Unknown;
        }
    }

    public static SymbolGlyph ForAnalyzer(ReferenceAnalyzerKind kind)
    {
        switch (kind)
        {
            case ReferenceAnalyzerKind.Uses:
                return SymbolGlyph.OutgoingBranch;

            case ReferenceAnalyzerKind.UsedBy:
            case ReferenceAnalyzerKind.ReadBy:
            case ReferenceAnalyzerKind.AssignedBy:
            case ReferenceAnalyzerKind.InstantiatedBy:
            case ReferenceAnalyzerKind.ExposedBy:
            case ReferenceAnalyzerKind.AppliedTo:
                return SymbolGlyph.IncomingBranch;

            default:
                return SymbolGlyph.HierarchyBranch;
        }
    }
}
