namespace RoslynQuery.Mcp.Contracts;

/// <summary>
/// What a Search/Replace predicate runs against. The canonical definition - RoslynQuery (the VSIX)
/// references it from here too, rather than keeping a second copy in sync by hand.
/// </summary>
public enum TargetKind
{
    SyntaxNode,
    SyntaxToken,
    Operation
}

/// <summary>How wide a Search/Replace run reaches. See <see cref="TargetKind"/>'s remark.</summary>
public enum ScopeKind
{
    ContainingMember,
    ContainingType,
    Document,
    Project,
    Solution
}
