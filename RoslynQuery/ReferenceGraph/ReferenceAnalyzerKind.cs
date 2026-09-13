namespace RoslynQuery.ReferenceGraph;

/// <summary>One branch of the analyzer tree, named after the ILSpy Analyzer pane this mirrors.</summary>
internal enum ReferenceAnalyzerKind
{
    Uses,
    UsedBy,
    ReadBy,
    AssignedBy,
    InstantiatedBy,
    ExposedBy,
    AppliedTo,
    Overrides,
    OverriddenBy,
    Implements,
    ImplementedBy,
    DerivedTypes,
    ExtensionMethods,
    Contains
}

internal static class ReferenceAnalyzerKinds
{
    public static string Header(this ReferenceAnalyzerKind kind)
    {
        switch (kind)
        {
            case ReferenceAnalyzerKind.Uses: return "Uses";
            case ReferenceAnalyzerKind.UsedBy: return "Used By";
            case ReferenceAnalyzerKind.ReadBy: return "Read By";
            case ReferenceAnalyzerKind.AssignedBy: return "Assigned By";
            case ReferenceAnalyzerKind.InstantiatedBy: return "Instantiated By";
            case ReferenceAnalyzerKind.ExposedBy: return "Exposed By";
            case ReferenceAnalyzerKind.AppliedTo: return "Applied To";
            case ReferenceAnalyzerKind.Overrides: return "Overrides";
            case ReferenceAnalyzerKind.OverriddenBy: return "Overridden By";
            case ReferenceAnalyzerKind.Implements: return "Implements";
            case ReferenceAnalyzerKind.ImplementedBy: return "Implemented By";
            case ReferenceAnalyzerKind.DerivedTypes: return "Derived Types";
            case ReferenceAnalyzerKind.ExtensionMethods: return "Extension Methods";
            case ReferenceAnalyzerKind.Contains: return "Contains";
            default: return kind.ToString();
        }
    }
}
