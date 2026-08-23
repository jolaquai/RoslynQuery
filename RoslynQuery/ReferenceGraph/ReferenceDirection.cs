namespace RoslynQuery.ReferenceGraph;

/// <summary>Which way a branch of the graph walks. A branch keeps its direction all the way down.</summary>
internal enum ReferenceDirection
{
    Incoming,
    Outgoing
}
