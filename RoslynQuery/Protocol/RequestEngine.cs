using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

using RoslynQuery.Query;
using RoslynQuery.Replace;

namespace RoslynQuery.Protocol;

/// <summary>A loaded solution to run against: what MSBuildWorkspace produced headless, or the live VisualStudioWorkspace.</summary>
internal sealed class Session
{
    public Workspace Workspace { get; set; }
    public Solution Solution { get; set; }

    /// <summary>Reported paths are relative to this.</summary>
    public string Root { get; set; }
    public string Source { get; set; }
    public List<string> Warnings { get; set; }
}

/// <summary>What a host needs to write an accepted plan.</summary>
internal sealed class ApplyContext
{
    public Session Session { get; set; }
    public IReadOnlyList<ReplacementItem> Items { get; set; }

    /// <summary>The plan whose diffs were reviewed; a live host commits exactly this one.</summary>
    public ChangeApplier.ApplyPlan Plan { get; set; }

    public CancellationToken CancellationToken { get; set; }

    /// <summary>Changed files by full path, ordinal-sorted. Linked documents are one entry.</summary>
    public SortedDictionary<string, (SourceText Old, SourceText New)> Files { get; set; }
}

/// <summary>
/// The whole request pipeline, host-agnostic: compile, scope, scan, optionally plan a replace and hand the plan to the
/// host to write. The CLI hosts it over an MSBuildWorkspace, the extension's bridge over VisualStudioWorkspace.
/// </summary>
internal static class RequestEngine
{
    public static async Task<RunResponse> RunAsync(
        RunRequest request,
        Func<CancellationToken, Task<Session>> open,
        Func<ApplyContext, Task> apply,
        CancellationToken cancellationToken)
    {
        var response = new RunResponse();

        var error = Validate(request);
        if (error != null) return Fail(response, error);

        // Compile before the workspace opens: a typo in the predicate fails in about a second instead of after the
        // solution load, which is the loop an agent spends most of its time in.
        Delegate predicate, transform = null;
        try
        {
            predicate = PredicateCompiler.Compile(request.Target, request.Query);
            if (request.Replace != null) transform = ReplaceCompiler.Compile(request.Target, request.Replace);
        }
        catch (PredicateCompilationException ex) { return Fail(response, "Compile error: " + ex.Message); }
        catch (NotSupportedException ex) { return Fail(response, ex.Message); }

        Session session;
        try
        {
            session = await open(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Fail(response, ex.Message); }

        response.Source = session.Source;
        if (session.Warnings is { Count: > 0 }) response.WorkspaceWarnings = [.. session.Warnings.Distinct()];

        var solution = session.Solution;
        var root = session.Root;

        var units = await ResolveScopeAsync(solution, root, request, cancellationToken).ConfigureAwait(false);
        if (units is null) return Fail(response, ScopeError(request));

        var found = new List<QueryHit>();
        var outcome = await QueryEngine.RunAsync(
            units, request.Target, request.Query, predicate, request.Max,
            batch => { lock (found) found.AddRange(batch); }, cancellationToken).ConfigureAwait(false);

        // QueryEngine treats a cancel as "stop here, keep what was found"; a cancelled request must not go on to plan or write.
        cancellationToken.ThrowIfCancellationRequested();

        found.Sort((x, y) =>
        {
            var c = string.CompareOrdinal(x.FilePath, y.FilePath);
            return c != 0 ? c : x.Span.Start != y.Span.Start ? x.Span.Start.CompareTo(y.Span.Start) : y.Span.End.CompareTo(x.Span.End);
        });

        response.Ok = true;
        response.Stats = new RunStats
        {
            Documents = outcome.Documents,
            Examined = outcome.Examined,
            Matched = outcome.Matched,
            Errors = outcome.Errors,
            FirstError = outcome.FirstError,
            Truncated = outcome.Truncated,
            ElapsedMs = (long)outcome.Elapsed.TotalMilliseconds
        };

        if (transform is null)
        {
            response.Hits = found.ConvertAll(h => new Hit
            {
                File = Rel(root, h.FilePath),
                Line = h.Line + 1,
                Column = h.Column + 1,
                EndLine = h.EndLine + 1,
                EndColumn = h.EndColumn + 1,
                Kind = h.Kind,
                Preview = h.Preview
            });
            return response;
        }

        await PlanAsync(response, request, session, found, transform, apply, cancellationToken).ConfigureAwait(false);
        return response;
    }

    private static async Task PlanAsync(
        RunResponse response, RunRequest request, Session session, List<QueryHit> found, Delegate transform,
        Func<ApplyContext, Task> apply, CancellationToken cancellationToken)
    {
        var solution = session.Solution;
        var root = session.Root;

        var items = await ReplaceEngine.GenerateAsync(solution, found, request.Target, transform, cancellationToken).ConfigureAwait(false);
        ReplaceEngine.MarkConflicts(items);

        response.Replacements = [.. items.Select(i => new Replacement
        {
            File = Rel(root, i.Hit.FilePath),
            Line = i.Hit.Line + 1,
            Column = i.Hit.Column + 1,
            Before = i.Before,
            After = i.After,
            Warning = i.Warning,
            Included = i.Included && i.After != null
        })];

        var plan = await ChangeApplier.PlanAsync(session.Workspace, solution, items, cancellationToken).ConfigureAwait(false);
        var warnings = (List<string>)plan.Outcome.Warnings;
        if (warnings.Count > 0) response.Warnings = warnings;

        // One entry per file on disk: linked/multi-targeted documents share a path and only the edited copy differs.
        var files = new SortedDictionary<string, (SourceText Old, SourceText New)>(StringComparer.Ordinal);
        foreach (var id in plan.ChangedDocuments)
        {
            var before = solution.GetDocument(id);
            var after = plan.Solution.GetDocument(id);
            var old = plan.BaseTexts.TryGetValue(id, out var planned) ? planned : await before.GetTextAsync(cancellationToken).ConfigureAwait(false);
            files[before.FilePath] = (old, await after.GetTextAsync(cancellationToken).ConfigureAwait(false));
        }

        var digest = new StringBuilder();
        response.Changes = [];
        foreach (var pair in files)
        {
            var rel = Rel(root, pair.Key);
            response.Changes.Add(new FileChange { File = rel, Diff = UnifiedDiff.Create(rel, pair.Value.Old, pair.Value.New) });
            digest.Append(rel).Append('\0').Append(Hash(pair.Value.Old)).Append('\0').Append(Hash(pair.Value.New)).Append('\n');
        }

        response.PlanId = Hex(Sha256(digest.ToString()), 8);

        if (!request.Apply) return;

        if (!string.Equals(request.ExpectPlan, response.PlanId, StringComparison.OrdinalIgnoreCase))
        {
            Fail(response, $"Plan mismatch: expected {request.ExpectPlan}, this run plans {response.PlanId}. The sources or the query changed since the dry run; review the diff and pass the new planId. Nothing was written.");
            response.ExitCode = 3;
            return;
        }

        if (files.Count == 0) return;

        try
        {
            await apply(new ApplyContext { Session = session, Items = items, Plan = plan, Files = files, CancellationToken = cancellationToken }).ConfigureAwait(false);
            response.Applied = true;
        }
        catch (ApplyRefusedException ex)
        {
            Fail(response, ex.Message);
            response.ExitCode = 3;
        }
        catch (ApplyFailedException ex)
        {
            Fail(response, ex.Message);
        }
    }

    private static string Hash(SourceText text) => Hex(Sha256(text.ToString()), 32);

    private static byte[] Sha256(string text)
    {
        using var sha = SHA256.Create();
        return sha.ComputeHash(Encoding.UTF8.GetBytes(text));
    }

    private static string Hex(byte[] bytes, int count)
    {
        var sb = new StringBuilder(count * 2);
        for (var i = 0; i < count; i++) sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }

    private static string Validate(RunRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Query)) return "No query given.";
        if (r.Max < 1) return "Max must be at least 1.";
        if (r.Apply && r.Replace is null) return "Apply needs a Replace.";
        if (r.Apply && string.IsNullOrWhiteSpace(r.ExpectPlan)) return "Apply needs ExpectPlan: run the replace without apply, review the diff, and pass its planId (--expect <planId>). Nothing was written.";
        if (r.Replace != null && r.Target == TargetKind.Operation) return "Replace does not support Target=Operation; use SyntaxNode or SyntaxToken.";
        if ((r.Scope is ScopeKind.Document or ScopeKind.ContainingType or ScopeKind.ContainingMember) && string.IsNullOrEmpty(r.File)) return $"Scope {r.Scope} needs a File.";
        if (r.Scope == ScopeKind.Project && string.IsNullOrEmpty(r.File) && string.IsNullOrEmpty(r.Project)) return "Scope Project needs a Project or a File inside it.";
        return null;
    }

    private static string ScopeError(RunRequest r) =>
        r.Scope == ScopeKind.Project && string.IsNullOrEmpty(r.File)
            ? $"No C# project named or located at '{r.Project}' in the solution."
            : $"'{r.File}' is not a C# document of the loaded solution, or no declaration encloses {r.Line},{r.Column}.";

    private static async Task<IReadOnlyList<ScopeUnit>> ResolveScopeAsync(Solution solution, string root, RunRequest r, CancellationToken cancellationToken)
    {
        if (r.Scope == ScopeKind.Project && string.IsNullOrEmpty(r.File))
        {
            var full = Path.GetFullPath(r.Project);
            var project = solution.Projects.FirstOrDefault(p =>
                string.Equals(p.Name, r.Project, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.FilePath, full, StringComparison.OrdinalIgnoreCase) ||
                p.FilePath != null && string.Equals(Path.GetFileNameWithoutExtension(p.FilePath), r.Project, StringComparison.OrdinalIgnoreCase));
            if (project is null) return null;

            // A multi-targeted project is one Roslyn project per framework, all sharing the path.
            var same = solution.Projects.Where(p => p.FilePath == project.FilePath);
            return await ScopeResolver.CollectAsync(same, r.IncludeGenerated, cancellationToken).ConfigureAwait(false);
        }

        var active = r.File is null ? null : new ActiveContext
        {
            FilePath = ResolveFile(root, r.File),
            Line = Math.Max(0, r.Line - 1),
            Column = Math.Max(0, r.Column - 1)
        };

        var units = await ScopeResolver.ResolveAsync(solution, r.Scope, active, r.IncludeGenerated, cancellationToken).ConfigureAwait(false);
        return units.Count == 0 && r.Scope != ScopeKind.Solution ? null : units;
    }

    /// <summary>A relative path is taken against the working directory, else against the solution folder (hit paths are relative to it).</summary>
    private static string ResolveFile(string root, string file)
    {
        var full = Path.GetFullPath(file);
        if (Path.IsPathRooted(file) || File.Exists(full) || string.IsNullOrEmpty(root)) return full;

        var underRoot = Path.GetFullPath(Path.Combine(root, file));
        return File.Exists(underRoot) ? underRoot : full;
    }

    /// <summary>Path relative to <paramref name="root"/> with forward slashes; the absolute path when it is outside of it.</summary>
    public static string Rel(string root, string path)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return path;

        var prefix = root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.StartsWith(prefix, comparison) ? path.Substring(prefix.Length).Replace('\\', '/') : path.Replace('\\', '/');
    }

    private static RunResponse Fail(RunResponse response, string error)
    {
        response.Ok = false;
        response.Error = error;
        response.ExitCode = 1;
        return response;
    }
}
