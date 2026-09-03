using System.Collections.Generic;

namespace RoslynQuery.Mcp.Contracts;

/// <summary>
/// Step one of the two-step replace: run the same search a <see cref="SearchRequest"/> would, then
/// generate a previewed transform for every hit. The result carries a <c>PreviewId</c> the client
/// hands back to <see cref="ReplaceApplyRequest"/> to commit a chosen subset.
/// </summary>
public sealed class ReplacePreviewRequest
{
    public SearchRequest Search { get; set; }

    /// <summary>The replacement transform text the Replace box takes - an expression or a statement body over (n|t, model, doc).</summary>
    public string Replacement { get; set; }
}

public sealed class ReplacePreviewResponse
{
    /// <summary>Null when nothing was generated (no hits, or every hit skipped): there is nothing to apply.</summary>
    public string PreviewId { get; set; }

    public IReadOnlyList<ReplacementPreviewDto> Items { get; set; }

    public int Examined { get; set; }
    public int Errors { get; set; }
    public string FirstError { get; set; }
    public bool Truncated { get; set; }

    /// <summary>How many items are selected by default - <see cref="ReplacementPreviewDto.Included"/> is true.</summary>
    public int IncludedCount { get; set; }
}

public sealed class ReplacementPreviewDto
{
    /// <summary>Position in <see cref="ReplacePreviewResponse.Items"/>; the handle <see cref="ReplaceApplyRequest.Indices"/> uses.</summary>
    public int Index { get; set; }

    public string FilePath { get; set; }
    public string FileName { get; set; }
    public int Line { get; set; }
    public int Column { get; set; }
    public string Kind { get; set; }

    public string Before { get; set; }

    /// <summary>Null when the hit was skipped - see <see cref="Warning"/>.</summary>
    public string After { get; set; }

    /// <summary>A null-result skip, an overlap with another included item, a stale span, or an exception message.</summary>
    public string Warning { get; set; }

    /// <summary>Default selection state: true only when the item has an <see cref="After"/> and no <see cref="Warning"/>.</summary>
    public bool Included { get; set; }
}

/// <summary>Step two: commit a subset of a previously generated preview.</summary>
public sealed class ReplaceApplyRequest
{
    public string PreviewId { get; set; }

    /// <summary>
    /// Which items to apply, by <see cref="ReplacementPreviewDto.Index"/>. Null applies every
    /// default-included item; an explicit list overrides the defaults exactly.
    /// </summary>
    public IReadOnlyList<int> Indices { get; set; }
}

public sealed class ReplaceApplyResponse
{
    /// <summary>False when the <c>PreviewId</c> is unknown or has expired; nothing was applied.</summary>
    public bool Found { get; set; }

    public int Applied { get; set; }
    public int Skipped { get; set; }
    public IReadOnlyList<string> Warnings { get; set; }
}
