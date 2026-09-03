namespace RoslynQuery.Mcp.Contracts;

public sealed class SearchRequest
{
    public TargetKind Target { get; set; }
    public ScopeKind Scope { get; set; }

    /// <summary>Required for every scope narrower than Project.</summary>
    public string FilePath { get; set; }

    /// <summary>0-based. Required for ContainingMember/ContainingType.</summary>
    public int? Line { get; set; }
    public int? Column { get; set; }

    /// <summary>The same predicate text the Find box takes - a bool expression or a statement body.</summary>
    public string Predicate { get; set; }

    public int Cap { get; set; } = 500;
    public bool IncludeGenerated { get; set; }
}
