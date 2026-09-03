namespace RoslynQuery.Query;

/// <summary>How the user's text is turned into the match method. Orthogonal to <c>TargetKind</c> (RoslynQuery.Mcp.Contracts).</summary>
internal enum PredicateMode
{
    /// <summary>A single bool expression, emitted as "return &lt;text&gt;;".</summary>
    Expression,

    /// <summary>Statements emitted as the method body verbatim, so locals and control flow work.</summary>
    Body
}
