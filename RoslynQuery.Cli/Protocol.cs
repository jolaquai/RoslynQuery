using System.Collections.Generic;
using System.Text.Json.Serialization;

using RoslynQuery.Query;

namespace RoslynQuery.Cli;

/// <summary>
/// One request, whichever way it arrives (CLI flags, <c>--request</c> JSON, later the in-VS pipe). Lines and columns
/// are 1-based, like every editor prints them. Enums serialize as their names, case-insensitively.
/// </summary>
public sealed class RunRequest
{
    /// <summary>.sln, .slnx or .csproj. Defaults to the only one in the working directory.</summary>
    public string Solution { get; set; }

    public TargetKind Target { get; set; } = TargetKind.SyntaxNode;
    public ScopeKind Scope { get; set; } = ScopeKind.Solution;

    /// <summary>Scope=Project without a File: project name or path.</summary>
    public string Project { get; set; }

    /// <summary>Required for Document, ContainingType, ContainingMember, and for Project when Project is not given.</summary>
    public string File { get; set; }
    public int Line { get; set; } = 1;
    public int Column { get; set; } = 1;

    /// <summary>The predicate, exactly as typed into the tool window (a bool expression, or statements).</summary>
    public string Query { get; set; }

    /// <summary>Replacement transform. Without it, the run only searches.</summary>
    public string Replace { get; set; }

    /// <summary>Write the replacement to disk. Without it a Replace is a dry run that only reports the diff.</summary>
    public bool Apply { get; set; }

    /// <summary>The <c>planId</c> of a dry run the caller reviewed; Apply refuses to write if this run plans anything else.</summary>
    public string ExpectPlan { get; set; }

    public int Max { get; set; } = 200;
    public bool IncludeGenerated { get; set; }
}

public sealed class RunResponse
{
    public bool Ok { get; set; }
    public string Error { get; set; }

    /// <summary>Workspace load problems (missing SDK, unresolved references, ...). Matches can be incomplete when this is not empty.</summary>
    public List<string> WorkspaceWarnings { get; set; }

    public RunStats Stats { get; set; }
    public List<Hit> Hits { get; set; }
    public List<Replacement> Replacements { get; set; }

    /// <summary>One unified diff per changed file. Present for a Replace run, applied or not.</summary>
    public List<FileChange> Changes { get; set; }

    /// <summary>Digest of exactly what a Replace run would write (paths, old and new content). Pass it back as ExpectPlan with Apply.</summary>
    public string PlanId { get; set; }
    public bool Applied { get; set; }
    public List<string> Warnings { get; set; }

    /// <summary>Process exit code: 0 ok, 1 failed (bad query, bad request, workspace), 3 Apply refused because the plan changed.</summary>
    [JsonIgnore]
    public int ExitCode { get; set; }
}

public sealed class RunStats
{
    public int Documents { get; set; }
    public int Examined { get; set; }
    public int Matched { get; set; }

    /// <summary>Candidates the predicate threw on.</summary>
    public int Errors { get; set; }
    public string FirstError { get; set; }

    /// <summary>Stopped at Max; more matches exist.</summary>
    public bool Truncated { get; set; }
    public long ElapsedMs { get; set; }
}

public sealed class Hit
{
    public string File { get; set; }
    public int Line { get; set; }
    public int Column { get; set; }
    public int EndLine { get; set; }
    public int EndColumn { get; set; }
    public string Kind { get; set; }
    public string Preview { get; set; }
}

public sealed class Replacement
{
    public string File { get; set; }
    public int Line { get; set; }
    public int Column { get; set; }
    public string Before { get; set; }
    public string After { get; set; }

    /// <summary>Why this match is not part of the change (null result, overlap, stale, predicate threw).</summary>
    public string Warning { get; set; }
    public bool Included { get; set; }
}

public sealed class FileChange
{
    public string File { get; set; }
    public string Diff { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(RunRequest))]
[JsonSerializable(typeof(RunResponse))]
internal sealed partial class CliJson : JsonSerializerContext;
