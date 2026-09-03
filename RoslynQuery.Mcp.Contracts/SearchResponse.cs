using System.Collections.Generic;

namespace RoslynQuery.Mcp.Contracts;

public sealed class SearchResponse
{
    public IReadOnlyList<HitDto> Hits { get; set; }
    public int Examined { get; set; }
    public int Errors { get; set; }
    public string FirstError { get; set; }
    public bool Truncated { get; set; }
}

public sealed class HitDto
{
    public string FilePath { get; set; }
    public string FileName { get; set; }
    public int Line { get; set; }
    public int Column { get; set; }
    public int EndLine { get; set; }
    public int EndColumn { get; set; }
    public string Kind { get; set; }
    public string Preview { get; set; }
}
