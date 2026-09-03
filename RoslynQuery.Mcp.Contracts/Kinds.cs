namespace RoslynQuery.Mcp.Contracts;

/// <summary>
/// Wire copy of RoslynQuery.Query.TargetKind. Kept as its own type, not a shared reference, so
/// either project's internals can change without the pipe protocol silently changing underneath it.
/// </summary>
public enum TargetKind
{
    SyntaxNode,
    SyntaxToken,
    Operation
}

/// <summary>Wire copy of RoslynQuery.Query.ScopeKind - see <see cref="TargetKind"/>'s remark.</summary>
public enum ScopeKind
{
    ContainingMember,
    ContainingType,
    Document,
    Project,
    Solution
}
