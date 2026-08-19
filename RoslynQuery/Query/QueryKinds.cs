namespace RoslynQuery.Query;

internal enum TargetKind
{
    SyntaxNode,
    SyntaxToken,
    Operation
}

/// <summary>How the user's text is turned into the match method. Orthogonal to <see cref="TargetKind"/>.</summary>
internal enum PredicateMode
{
    /// <summary>A single bool expression, emitted as "return &lt;text&gt;;".</summary>
    Expression,

    /// <summary>Statements emitted as the method body verbatim, so locals and control flow work.</summary>
    Body
}

internal enum ScopeKind
{
    ContainingMember,
    ContainingType,
    Document,
    Project,
    Solution
}
