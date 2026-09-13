namespace RoslynQuery.ReferenceGraph;

/// <summary>
/// What a row is. The tree alternates <see cref="Symbol"/> and <see cref="Analyzer"/> rows: a symbol
/// offers the branches applicable to it, and each branch fetches the symbols that answer it.
/// </summary>
internal enum NodeRole
{
    Symbol,
    Analyzer,
    Locations,
    Location,
    Message
}
