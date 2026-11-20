using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

using RoslynQuery.Query;
using RoslynQuery.Replace;

namespace RoslynQuery.Cli;

internal static class Runner
{
    public static async Task<RunResponse> RunAsync(RunRequest request, CancellationToken cancellationToken)
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

        var path = ResolveSolutionPath(request.Solution, out error);
        if (path == null) return Fail(response, error);

        var warnings = new List<string>();
        using var workspace = MSBuildWorkspace.Create();
        workspace.RegisterWorkspaceFailedHandler(e => { lock (warnings) warnings.Add(e.Diagnostic.Message); });

        Solution solution;
        try
        {
            solution = path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                ? (await workspace.OpenProjectAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false)).Solution
                : await workspace.OpenSolutionAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) { return Fail(response, $"Could not open '{path}': {ex.Message}"); }

        if (warnings.Count > 0) response.WorkspaceWarnings = [.. warnings.Distinct()];

        var root = Path.GetDirectoryName(path);

        var units = await ResolveScopeAsync(solution, request, cancellationToken).ConfigureAwait(false);
        if (units is null) return Fail(response, ScopeError(request));

        var found = new List<QueryHit>();
        var outcome = await QueryEngine.RunAsync(
            units, request.Target, request.Query, predicate, request.Max,
            batch => { lock (found) found.AddRange(batch); }, cancellationToken).ConfigureAwait(false);

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

        await PlanAsync(response, request, workspace, solution, found, transform, root, cancellationToken).ConfigureAwait(false);
        return response;
    }

    private static async Task PlanAsync(
        RunResponse response, RunRequest request, MSBuildWorkspace workspace, Solution solution,
        List<QueryHit> found, Delegate transform, string root, CancellationToken cancellationToken)
    {
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

        var plan = await ChangeApplier.PlanAsync(workspace, solution, items, cancellationToken).ConfigureAwait(false);
        var warnings = (List<string>)plan.Outcome.Warnings;
        if (warnings.Count > 0) response.Warnings = warnings;

        // One entry per file on disk: linked/multi-targeted documents share a path and only the edited copy differs.
        var files = new SortedDictionary<string, (SourceText Old, SourceText New)>(StringComparer.Ordinal);
        foreach (var id in plan.ChangedDocuments)
        {
            var before = solution.GetDocument(id);
            var after = plan.Solution.GetDocument(id);
            files[before.FilePath] = (await before.GetTextAsync(cancellationToken).ConfigureAwait(false), await after.GetTextAsync(cancellationToken).ConfigureAwait(false));
        }

        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        response.Changes = [];
        foreach (var (file, (oldText, newText)) in files)
        {
            var rel = Rel(root, file);
            response.Changes.Add(new FileChange { File = rel, Diff = UnifiedDiff.Create(rel, oldText, newText) });
            digest.AppendData(Encoding.UTF8.GetBytes(rel + "\0" + Hash(oldText) + "\0" + Hash(newText) + "\n"));
        }

        response.PlanId = Convert.ToHexStringLower(digest.GetHashAndReset().AsSpan(0, 8));

        if (!request.Apply || files.Count == 0) return;

        if (request.ExpectPlan != null && !string.Equals(request.ExpectPlan, response.PlanId, StringComparison.OrdinalIgnoreCase))
        {
            Fail(response, $"Plan mismatch: expected {request.ExpectPlan}, this run plans {response.PlanId}. The sources or the query changed since the dry run; review the diff and pass the new planId. Nothing was written.");
            response.ExitCode = 3;
            return;
        }

        // Re-read right before writing: the solution may have been open for a while.
        foreach (var (file, (oldText, _)) in files)
        {
            using var stream = File.OpenRead(file);
            if (SourceText.From(stream, oldText.Encoding).ToString() != oldText.ToString())
            {
                Fail(response, $"'{Rel(root, file)}' changed on disk since the solution was loaded. Nothing was written.");
                response.ExitCode = 3;
                return;
            }
        }

        foreach (var (file, (_, newText)) in files)
        {
            using var writer = new StreamWriter(file, append: false, newText.Encoding ?? new UTF8Encoding(false));
            newText.Write(writer);
        }

        response.Applied = true;
    }

    private static string Hash(SourceText text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));

    private static string Validate(RunRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Query)) return "No query given.";
        if (r.Max < 1) return "Max must be at least 1.";
        if (r.Apply && r.Replace is null) return "Apply needs a Replace.";
        if (r.Replace != null && r.Target == TargetKind.Operation) return "Replace does not support Target=Operation; use SyntaxNode or SyntaxToken.";
        if (r.Scope is ScopeKind.Document or ScopeKind.ContainingType or ScopeKind.ContainingMember && string.IsNullOrEmpty(r.File)) return $"Scope {r.Scope} needs a File.";
        if (r.Scope == ScopeKind.Project && string.IsNullOrEmpty(r.File) && string.IsNullOrEmpty(r.Project)) return "Scope Project needs a Project or a File inside it.";
        return null;
    }

    private static string ScopeError(RunRequest r) =>
        r.Scope == ScopeKind.Project && string.IsNullOrEmpty(r.File)
            ? $"No C# project named or located at '{r.Project}' in the solution."
            : $"'{r.File}' is not a C# document of the loaded solution, or no declaration encloses {r.Line},{r.Column}.";

    private static async Task<IReadOnlyList<ScopeUnit>> ResolveScopeAsync(Solution solution, RunRequest r, CancellationToken cancellationToken)
    {
        if (r.Scope == ScopeKind.Project && string.IsNullOrEmpty(r.File))
        {
            var full = Path.GetFullPath(r.Project);
            var project = solution.Projects.FirstOrDefault(p =>
                string.Equals(p.Name, r.Project, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.FilePath, full, StringComparison.OrdinalIgnoreCase));
            if (project is null) return null;

            // A multi-targeted project is one Roslyn project per framework, all sharing the path.
            var same = solution.Projects.Where(p => p.FilePath == project.FilePath);
            return await ScopeResolver.CollectAsync(same, r.IncludeGenerated, cancellationToken).ConfigureAwait(false);
        }

        var active = r.File is null ? null : new ActiveContext
        {
            FilePath = Path.GetFullPath(r.File),
            Line = Math.Max(0, r.Line - 1),
            Column = Math.Max(0, r.Column - 1)
        };

        var units = await ScopeResolver.ResolveAsync(solution, r.Scope, active, r.IncludeGenerated, cancellationToken).ConfigureAwait(false);
        return units.Count == 0 && r.Scope != ScopeKind.Solution ? null : units;
    }

    private static string ResolveSolutionPath(string given, out string error)
    {
        error = null;
        if (!string.IsNullOrEmpty(given))
        {
            var full = Path.GetFullPath(given);
            if (File.Exists(full)) return full;
            error = $"'{given}' does not exist.";
            return null;
        }

        var cwd = Directory.GetCurrentDirectory();
        foreach (var pattern in (string[])["*.slnx", "*.sln", "*.csproj"])
        {
            var hits = Directory.GetFiles(cwd, pattern);
            if (hits.Length == 1) return hits[0];
            if (hits.Length > 1)
            {
                error = $"Several {pattern} files in {cwd}; pass --solution.";
                return null;
            }
        }

        error = $"No solution or project in {cwd}; pass --solution.";
        return null;
    }

    private static string Rel(string root, string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        var rel = Path.GetRelativePath(root, path);
        return (rel.StartsWith("..", StringComparison.Ordinal) ? path : rel).Replace('\\', '/');
    }

    private static RunResponse Fail(RunResponse response, string error)
    {
        response.Ok = false;
        response.Error = error;
        response.ExitCode = 1;
        return response;
    }
}
