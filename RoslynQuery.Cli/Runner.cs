using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

using RoslynQuery.Protocol;

namespace RoslynQuery.Cli;

/// <summary>Headless host: MSBuildWorkspace in, files on disk out.</summary>
internal static class Runner
{
    public static async Task<RunResponse> RunAsync(RunRequest request, CancellationToken cancellationToken)
    {
        MSBuildWorkspace workspace = null;
        try
        {
            var response = await RequestEngine.RunAsync(request, ct => OpenAsync(request, w => workspace = w, ct), ApplyAsync, cancellationToken).ConfigureAwait(false);
            response.Source ??= "headless";
            return response;
        }
        finally
        {
            workspace?.Dispose();
        }
    }

    private static async Task<Session> OpenAsync(RunRequest request, Action<MSBuildWorkspace> own, CancellationToken cancellationToken)
    {
        var path = ResolveSolutionPath(request.Solution);

        var warnings = new List<string>();
        var workspace = MSBuildWorkspace.Create();
        own(workspace);
        workspace.RegisterWorkspaceFailedHandler(e => { lock (warnings) warnings.Add(e.Diagnostic.Message); });

        Solution solution;
        try
        {
            solution = path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                ? (await workspace.OpenProjectAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false)).Solution
                : await workspace.OpenSolutionAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Could not open '{path}': {ex.Message}", ex);
        }

        return new Session { Workspace = workspace, Solution = solution, Root = Path.GetDirectoryName(path), Source = "headless", Warnings = warnings };
    }

    private static Task ApplyAsync(ApplyContext context)
    {
        // Re-read right before writing: the solution may have been open for a while.
        foreach (var pair in context.Files)
        {
            using var stream = File.OpenRead(pair.Key);
            if (SourceText.From(stream, pair.Value.Old.Encoding).ToString() != pair.Value.Old.ToString())
                throw new ApplyRefusedException($"'{RequestEngine.Rel(context.Session.Root, pair.Key)}' changed on disk since the solution was loaded. Nothing was written.");
        }

        // Every file is written to a temp file first, then swapped in; a failure part way puts the already swapped ones back.
        var stamp = Guid.NewGuid().ToString("N").Substring(0, 8);
        var staged = new List<(string Target, string Temp)>();
        var swapped = new List<(string Target, string Backup)>();
        try
        {
            foreach (var pair in context.Files)
            {
                var temp = pair.Key + ".rq-" + stamp + ".tmp";
                staged.Add((pair.Key, temp));
                using var writer = new StreamWriter(temp, append: false, pair.Value.New.Encoding ?? new UTF8Encoding(false));
                pair.Value.New.Write(writer);
            }

            foreach (var (target, temp) in staged)
            {
                var backup = target + ".rq-" + stamp + ".bak";
                File.Replace(temp, target, backup);
                swapped.Add((target, backup));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            foreach (var (target, backup) in swapped)
            {
                try { File.Copy(backup, target, overwrite: true); } catch (Exception restore) when (restore is IOException or UnauthorizedAccessException) { }
            }

            throw new ApplyRefusedException("Could not write every file (" + ex.Message + "); " + (swapped.Count > 0 ? swapped.Count + " already written file(s) were restored." : "nothing was written."));
        }
        finally
        {
            foreach (var (_, temp) in staged) TryDelete(temp);
            foreach (var (_, backup) in swapped) TryDelete(backup);
        }

        return Task.CompletedTask;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string ResolveSolutionPath(string given)
    {
        if (!string.IsNullOrEmpty(given))
        {
            var full = Path.GetFullPath(given);
            return File.Exists(full) ? full : throw new InvalidOperationException($"'{given}' does not exist.");
        }

        var cwd = Directory.GetCurrentDirectory();
        foreach (var pattern in (string[])["*.slnx", "*.sln", "*.csproj"])
        {
            var hits = Directory.GetFiles(cwd, pattern);
            if (hits.Length == 1) return hits[0];
            if (hits.Length > 1) throw new InvalidOperationException($"Several {pattern} files in {cwd}; pass --solution.");
        }

        throw new InvalidOperationException($"No solution or project in {cwd}; pass --solution.");
    }
}
