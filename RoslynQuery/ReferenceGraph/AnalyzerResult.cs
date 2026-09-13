using System.Collections.Generic;

namespace RoslynQuery.ReferenceGraph;

/// <summary>What one analyzer branch produced and how long it took, which is what its header reports.</summary>
internal readonly struct AnalyzerResult
{
    public AnalyzerResult(IReadOnlyList<ReferenceGraphNode> rows, long elapsedMilliseconds)
    {
        Rows = rows ?? [];
        ElapsedMilliseconds = elapsedMilliseconds;
    }

    public IReadOnlyList<ReferenceGraphNode> Rows { get; }
    public long ElapsedMilliseconds { get; }
}
