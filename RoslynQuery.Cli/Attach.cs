using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using RoslynQuery.Protocol;

namespace RoslynQuery.Cli;

/// <summary>The request reached the bridge but no reply came back; its outcome is unknown.</summary>
internal sealed class BridgeReplyLostException : Exception
{
    public BridgeReplyLostException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Client for the bridge a running Visual Studio hosts: same request, live workspace including unsaved edits.</summary>
internal static class Attach
{
    /// <summary>Null when no matching host is running.</summary>
    public static BridgeHostInfo Find(RunRequest request, out List<int> candidates, out BridgeMatch match)
    {
        candidates = [];
        match = BridgeMatch.None;
        var directory = BridgePaths.Directory;
        if (!Directory.Exists(directory)) return null;

        var wanted = string.IsNullOrEmpty(request.Solution) ? null : Path.GetFullPath(request.Solution);
        var cwd = Directory.GetCurrentDirectory();

        // A client started from inside a VS (its terminal, an agent panel) belongs to that VS, whatever else is open.
        var ancestors = ProcessTree.Ancestors();

        var hosts = new List<BridgeHostInfo>();

        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            BridgeHostInfo info;
            try { info = JsonSerializer.Deserialize(File.ReadAllText(file), CliJson.Default.BridgeHostInfo); }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { continue; }

            if (info?.Pipe is null || info.Solution is null) continue;
            if (!IsAlive(info.Pid))
            {
                try { File.Delete(file); } catch (IOException) { }
                continue;
            }

            hosts.Add(info);
        }

        // Without a solution argument, a working directory that has a solution of its own is for headless to open, not for a stranger's VS.
        var cwdHasSolution = wanted is null && new[] { "*.slnx", "*.sln", "*.csproj" }.Any(pattern => Directory.GetFiles(cwd, pattern).Length > 0);

        return BridgeSelection.Pick(hosts, wanted, cwd, ancestors, out candidates, out match, allowOnlyInstance: !cwdHasSolution);
    }

    /// <summary>Solutions that live bridges have open, "path (pid n)" each.</summary>
    public static List<string> LiveSolutions()
    {
        var result = new List<string>();
        var directory = BridgePaths.Directory;
        if (!Directory.Exists(directory)) return result;

        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                var info = JsonSerializer.Deserialize(File.ReadAllText(file), CliJson.Default.BridgeHostInfo);
                if (info?.Solution != null && IsAlive(info.Pid)) result.Add(info.Solution + " (pid " + info.Pid + ")");
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }

        return result;
    }

    /// <summary>The live hosts as JSON, for the MCP 'bridges' tool.</summary>
    public static string Describe()
    {
        var directory = BridgePaths.Directory;
        var hosts = new System.Collections.Generic.List<object>();
        if (Directory.Exists(directory))
        {
            var ancestors = ProcessTree.Ancestors();
            foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
            {
                try
                {
                    var info = JsonSerializer.Deserialize(File.ReadAllText(file), CliJson.Default.BridgeHostInfo);
                    if (info?.Solution != null && IsAlive(info.Pid)) hosts.Add(new { pid = info.Pid, solution = info.Solution, version = info.Version, startedUtc = info.StartedUtc, ancestor = ancestors.Contains(info.Pid) });
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
            }
        }

        return JsonSerializer.Serialize(hosts, new JsonSerializerOptions { TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() });
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            // A recycled pid belongs to some other process by now.
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.ProcessName.Equals("devenv", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>Hit paths come back relative to the solution folder, so a relative path that is not under the working directory is tried there.</summary>
    private static string ResolveFile(string file, string solution)
    {
        var full = Path.GetFullPath(file);
        if (File.Exists(full) || Path.IsPathRooted(file) || string.IsNullOrEmpty(solution)) return full;

        var underSolution = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(solution), file));
        return File.Exists(underSolution) ? underSolution : full;
    }

    public static async Task<RunResponse> RunAsync(BridgeHostInfo host, RunRequest request, CancellationToken cancellationToken)
    {
        // The live workspace holds the whole solution, so a project given as the solution means that project's scope.
        if (request.Scope == RoslynQuery.Query.ScopeKind.Solution && !string.IsNullOrEmpty(request.Solution) && request.Solution.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            request.Scope = RoslynQuery.Query.ScopeKind.Project;
            request.Project = Path.GetFullPath(request.Solution);
        }

        // The host resolves paths against its own working directory, which is not ours.
        if (!string.IsNullOrEmpty(request.File)) request.File = ResolveFile(request.File, host.Solution);
        if (!string.IsNullOrEmpty(request.Project) && File.Exists(request.Project)) request.Project = Path.GetFullPath(request.Project);

        using var pipe = new NamedPipeClientStream(".", host.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        }

        var utf8 = new UTF8Encoding(false);
        using var writer = new StreamWriter(pipe, utf8, 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        using var reader = new StreamReader(pipe, utf8, false, 4096, leaveOpen: true);

        await writer.WriteLineAsync(host.Token).ConfigureAwait(false);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, CliJson.Default.RunRequest)).ConfigureAwait(false);

        // From here on the bridge has the request: whatever it did, running it again elsewhere could do it twice.
        string line;
        try
        {
            line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new BridgeReplyLostException("The connection to the Visual Studio bridge broke after the request was sent: " + ex.Message, ex);
        }

        if (line is null) throw new BridgeReplyLostException("The Visual Studio bridge closed the connection without answering.", null);

        try
        {
            return JsonSerializer.Deserialize(line, CliJson.Default.RunResponse)
                ?? throw new BridgeReplyLostException("The Visual Studio bridge sent an empty response.", null);
        }
        catch (JsonException ex)
        {
            throw new BridgeReplyLostException("The Visual Studio bridge sent an unreadable response: " + ex.Message, ex);
        }
    }
}
