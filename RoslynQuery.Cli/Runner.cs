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

        foreach (var pair in context.Files)
        {
            using var writer = new StreamWriter(pair.Key, append: false, pair.Value.New.Encoding ?? new UTF8Encoding(false));
            pair.Value.New.Write(writer);
        }

        return Task.CompletedTask;
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
