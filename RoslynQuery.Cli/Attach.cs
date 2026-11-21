using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using RoslynQuery.Protocol;

namespace RoslynQuery.Cli;

/// <summary>Client for the bridge a running Visual Studio hosts: same request, live workspace including unsaved edits.</summary>
internal static class Attach
{
    /// <summary>Null when no matching host is running.</summary>
    public static BridgeHostInfo Find(RunRequest request, out List<int> candidates)
    {
        candidates = [];
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

        return BridgeSelection.Pick(hosts, wanted, cwd, ancestors, out candidates);
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
        try { return !Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }
    }

    public static async Task<RunResponse> RunAsync(BridgeHostInfo host, RunRequest request, CancellationToken cancellationToken)
    {
        // The host resolves paths against its own working directory, which is not ours.
        if (!string.IsNullOrEmpty(request.File)) request.File = Path.GetFullPath(request.File);
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

        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new IOException("The Visual Studio bridge closed the connection without answering.");

        return JsonSerializer.Deserialize(line, CliJson.Default.RunResponse)
            ?? throw new IOException("The Visual Studio bridge sent an empty response.");
    }
}
