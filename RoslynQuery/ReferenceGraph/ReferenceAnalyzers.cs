using System.Collections.Generic;

using Microsoft.CodeAnalysis;

namespace RoslynQuery.ReferenceGraph;

/// <summary>Which analyzer branches a symbol offers. Keyed on the symbol, not just its kind, so a static method stays at two branches while an override gets five.</summary>
internal static class ReferenceAnalyzers
{
    public static IReadOnlyList<ReferenceAnalyzerKind> For(ISymbol symbol)
    {
        switch (symbol)
        {
            case IMethodSymbol method
                when method.MethodKind == MethodKind.Constructor || method.MethodKind == MethodKind.StaticConstructor:
                return [ReferenceAnalyzerKind.Uses, ReferenceAnalyzerKind.UsedBy];

            // An enum member is a field that cannot be written, so Assigned By could only ever be empty.
            case IFieldSymbol enumMember when enumMember.ContainingType?.TypeKind == TypeKind.Enum:
                return [ReferenceAnalyzerKind.Uses, ReferenceAnalyzerKind.ReadBy];

            case IFieldSymbol _:
                return [ReferenceAnalyzerKind.Uses, ReferenceAnalyzerKind.AssignedBy, ReferenceAnalyzerKind.ReadBy];

            case IMethodSymbol _:
            case IPropertySymbol _:
            case IEventSymbol _:
                return Member(symbol);

            case INamedTypeSymbol type:
                return Type(type);

            default:
                return [];
        }
    }

    private static IReadOnlyList<ReferenceAnalyzerKind> Member(ISymbol symbol)
    {
        var kinds = new List<ReferenceAnalyzerKind> { ReferenceAnalyzerKind.Uses, ReferenceAnalyzerKind.UsedBy };

        // Roslyn reports every interface member as IsAbstract, so the override branches have to be
        // excluded by containing kind or an interface member would offer one that never resolves.
        var isInterfaceMember = symbol.ContainingType?.TypeKind == TypeKind.Interface;

        if (isInterfaceMember)
        {
            kinds.Add(ReferenceAnalyzerKind.ImplementedBy);
            return kinds;
        }

        if ((symbol.IsVirtual || symbol.IsAbstract || symbol.IsOverride) && !symbol.IsSealed)
            kinds.Add(ReferenceAnalyzerKind.OverriddenBy);

        if (symbol.IsOverride) kinds.Add(ReferenceAnalyzerKind.Overrides);
        if (ImplementsAnyInterfaceMember(symbol)) kinds.Add(ReferenceAnalyzerKind.Implements);

        return kinds;
    }

    private static IReadOnlyList<ReferenceAnalyzerKind> Type(INamedTypeSymbol type)
    {
        var kinds = new List<ReferenceAnalyzerKind> { ReferenceAnalyzerKind.Uses };

        if (!type.IsStatic
            && (type.TypeKind == TypeKind.Class || type.TypeKind == TypeKind.Struct || type.TypeKind == TypeKind.Delegate))
            kinds.Add(ReferenceAnalyzerKind.InstantiatedBy);

        kinds.Add(ReferenceAnalyzerKind.UsedBy);
        kinds.Add(ReferenceAnalyzerKind.ExposedBy);

        if (type.TypeKind == TypeKind.Class || type.TypeKind == TypeKind.Interface)
            kinds.Add(ReferenceAnalyzerKind.DerivedTypes);

        if (type.TypeKind == TypeKind.Interface) kinds.Add(ReferenceAnalyzerKind.ImplementedBy);

        kinds.Add(ReferenceAnalyzerKind.ExtensionMethods);

        if (IsAttribute(type)) kinds.Add(ReferenceAnalyzerKind.AppliedTo);

        return kinds;
    }

    /// <summary>Compares override roots, so an override several levels below the implementing member still reports Implements.</summary>
    private static bool ImplementsAnyInterfaceMember(ISymbol symbol)
    {
        var containing = symbol.ContainingType;
        if (containing is null) return false;

        var root = OverrideRoot(symbol);

        foreach (var contract in containing.AllInterfaces)
        {
            foreach (var member in contract.GetMembers())
            {
                var implementation = containing.FindImplementationForInterfaceMember(member);
                if (implementation is null) continue;

                if (SymbolEqualityComparer.Default.Equals(OverrideRoot(implementation), root)) return true;
            }
        }

        return false;
    }

    private static ISymbol OverrideRoot(ISymbol symbol)
    {
        for (var current = symbol; current != null;)
        {
            var overridden = Overridden(current);
            if (overridden is null) return current;

            current = overridden;
        }

        return symbol;
    }

    private static ISymbol Overridden(ISymbol symbol)
    {
        switch (symbol)
        {
            case IMethodSymbol method: return method.OverriddenMethod;
            case IPropertySymbol property: return property.OverriddenProperty;
            case IEventSymbol @event: return @event.OverriddenEvent;
            default: return null;
        }
    }

    private static bool IsAttribute(INamedTypeSymbol type)
    {
        for (var current = type.BaseType; current != null; current = current.BaseType)
        {
            if (current.MetadataName == "Attribute" && current.ContainingNamespace?.ToDisplayString() == "System")
                return true;
        }

        return false;
    }
}
