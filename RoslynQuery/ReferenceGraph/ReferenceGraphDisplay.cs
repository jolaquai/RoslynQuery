using System.Collections.Generic;
using System.Collections.Immutable;

using Microsoft.CodeAnalysis;

namespace RoslynQuery.ReferenceGraph;

/// <summary>How a symbol is spelled on a graph row. Shared so a root reads like its own children.</summary>
internal static class ReferenceGraphDisplay
{
    private static readonly SymbolDisplayFormat Format = new SymbolDisplayFormat(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters
            | SymbolDisplayMemberOptions.IncludeContainingType
            | SymbolDisplayMemberOptions.IncludeExplicitInterface,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static readonly SymbolDisplayFormat QualifiedType = new SymbolDisplayFormat(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static readonly SymbolDisplayFormat ShortType = new SymbolDisplayFormat(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static readonly SymbolDisplayFormat MemberOnly = new SymbolDisplayFormat(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeExplicitInterface,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType
            | SymbolDisplayParameterOptions.IncludeParamsRefOut
            | SymbolDisplayParameterOptions.IncludeExtensionThis,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    /// <summary>The short label, used for sorting, tests and the status line.</summary>
    public static string Of(ISymbol symbol) => symbol is null ? string.Empty : symbol.ToDisplayString(Format);

    /// <summary>
    /// ILSpy's spelling as classified runs: the container fully qualified, the member with short parameter
    /// types, then " : " and the declared type. A single <see cref="SymbolDisplayFormat"/> cannot mix
    /// qualification styles, so the row is composed.
    /// </summary>
    public static IReadOnlyList<SignaturePart> SignatureOf(ISymbol symbol)
    {
        if (symbol is null) return [];

        var parts = new List<SignaturePart>();

        if (symbol is INamespaceOrTypeSymbol)
        {
            Append(parts, symbol.ToDisplayParts(QualifiedType));
            return parts;
        }

        if (symbol.ContainingType != null)
        {
            Append(parts, symbol.ContainingType.ToDisplayParts(QualifiedType));
            parts.Add(new SignaturePart(SymbolDisplayPartKind.Punctuation, "."));
        }

        Append(parts, symbol.ToDisplayParts(MemberOnly));

        var type = DeclaredTypeOf(symbol);
        if (type is null) return parts;

        parts.Add(new SignaturePart(SymbolDisplayPartKind.Space, " "));
        parts.Add(new SignaturePart(SymbolDisplayPartKind.Punctuation, ":"));
        parts.Add(new SignaturePart(SymbolDisplayPartKind.Space, " "));
        Append(parts, type.ToDisplayParts(ShortType));

        return parts;
    }

    private static void Append(List<SignaturePart> parts, ImmutableArray<SymbolDisplayPart> source)
    {
        foreach (var part in source) parts.Add(new SignaturePart(part.Kind, part.ToString()));
    }

    /// <summary>A conversion already names its type, and an enum member's type is its container.</summary>
    private static ITypeSymbol DeclaredTypeOf(ISymbol symbol)
    {
        switch (symbol)
        {
            case IMethodSymbol method:
                switch (method.MethodKind)
                {
                    case MethodKind.Constructor:
                    case MethodKind.StaticConstructor:
                    case MethodKind.Destructor:
                    case MethodKind.Conversion:
                        return null;
                    default:
                        return method.ReturnType;
                }

            case IPropertySymbol property: return property.Type;
            case IFieldSymbol field: return field.ContainingType?.TypeKind == TypeKind.Enum ? null : field.Type;
            case IEventSymbol @event: return @event.Type;
            default: return null;
        }
    }
}
