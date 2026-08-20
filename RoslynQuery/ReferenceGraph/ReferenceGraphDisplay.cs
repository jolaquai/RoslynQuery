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

    public static string Of(ISymbol symbol) => symbol is null ? string.Empty : symbol.ToDisplayString(Format);
}
