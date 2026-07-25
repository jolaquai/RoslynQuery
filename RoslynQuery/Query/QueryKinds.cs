namespace RoslynQuery.Query;

internal enum TargetKind
{
    SyntaxNode,
    SyntaxToken,
    Operation
}

internal enum ScopeKind
{
    ContainingMember,
    ContainingType,
    Document,
    Project,
    Solution
}
