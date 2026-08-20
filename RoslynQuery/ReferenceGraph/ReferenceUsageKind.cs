using System;

namespace RoslynQuery.ReferenceGraph;

/// <summary>
/// How one reference occurrence uses its target. Flags, because a few occurrences are genuinely
/// both (a compound assignment reads and writes in one expression).
/// </summary>
[Flags]
internal enum ReferenceUsageKind
{
    None = 0,
    Invocation = 1,
    Read = 2,
    Write = 4,
    Construction = 8,
    TypeReference = 16
}
