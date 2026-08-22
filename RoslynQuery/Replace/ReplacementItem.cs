using RoslynQuery.Query;

namespace RoslynQuery.Replace;

/// <summary>
/// A generated replacement for one <see cref="QueryHit"/>. Mutable (<see cref="Included"/> is
/// toggled by the user and by conflict detection), unlike <see cref="QueryHit"/> itself.
/// </summary>
internal sealed class ReplacementItem
{
    public QueryHit Hit { get; set; }

    /// <summary>The hit's own preview text, reused verbatim so before/after line up.</summary>
    public string Before { get; set; }

    /// <summary>
    /// The replacement text: the string result verbatim, or <c>NormalizeWhitespace().ToFullString()</c>
    /// for a SyntaxNode/SyntaxToken result. Null when the hit was skipped (see <see cref="Warning"/>).
    /// </summary>
    public string After { get; set; }

    public bool Included { get; set; } = true;

    /// <summary>Null-result skip, an overlap with another included item, a stale span, or an exception message.</summary>
    public string Warning { get; set; }
}
