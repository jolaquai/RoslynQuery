using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RoslynQuery.Protocol;

internal enum BridgeMatch { None, Solution, Ancestor, WorkingDirectory, OnlyInstance }

/// <summary>Which of several live Visual Studio bridges a client should talk to.</summary>
internal static class BridgeSelection
{
    private const int Exact = 2_000_000;
    private const int SameFolder = 1_999_999;
    private const int Nested = 1_000_000;

    /// <summary>
    /// With an explicit solution: the best match, a folder containing the other counting as one. Without: the VS this process
    /// was started from, else the solution containing the working directory, else the only open instance. Ties go to the newest.
    /// <paramref name="candidates"/> are the pids matching the chosen one equally well (the chosen pid included).
    /// </summary>
    public static BridgeHostInfo Pick(IReadOnlyList<BridgeHostInfo> hosts, string wantedSolution, string cwd, ISet<int> ancestors, out List<int> candidates, out BridgeMatch match, bool allowOnlyInstance = true)
    {
        candidates = [];
        match = BridgeMatch.None;

        if (wantedSolution != null)
        {
            var best = Best(hosts, h => Score(h.Solution, wantedSolution, cwd), ancestors, out var score);
            if (best is null) return null;

            candidates = hosts.Where(h => Score(h.Solution, wantedSolution, cwd) == score).Select(h => h.Pid).ToList();
            match = BridgeMatch.Solution;
            return best;
        }

        var launchers = hosts.Where(h => ancestors.Contains(h.Pid)).ToList();
        if (launchers.Count > 0)
        {
            var best = Best(launchers, h => Math.Max(1, Score(h.Solution, null, cwd)), ancestors, out _);
            candidates = launchers.Select(h => h.Pid).ToList();
            match = BridgeMatch.Ancestor;
            return best;
        }

        var byDirectory = Best(hosts, h => Score(h.Solution, null, cwd), ancestors, out var directoryScore);
        if (byDirectory != null)
        {
            candidates = hosts.Where(h => Score(h.Solution, null, cwd) == directoryScore).Select(h => h.Pid).ToList();
            match = BridgeMatch.WorkingDirectory;
            return byDirectory;
        }

        if (hosts.Count == 1 && allowOnlyInstance)
        {
            candidates = [hosts[0].Pid];
            match = BridgeMatch.OnlyInstance;
            return hosts[0];
        }

        return null;
    }

    private static BridgeHostInfo Best(IReadOnlyList<BridgeHostInfo> hosts, Func<BridgeHostInfo, int> score, ISet<int> ancestors, out int bestScore)
    {
        BridgeHostInfo best = null;
        bestScore = 0;
        foreach (var info in hosts)
        {
            var s = score(info);
            if (s == 0) continue;
            if (best is null || s > bestScore || s == bestScore && Prefer(info, best, ancestors))
            {
                best = info;
                bestScore = s;
            }
        }

        return best;
    }

    // Equal matches: the one this process was started from, then the newest.
    private static bool Prefer(BridgeHostInfo info, BridgeHostInfo best, ISet<int> ancestors)
    {
        var a = ancestors.Contains(info.Pid);
        var b = ancestors.Contains(best.Pid);
        if (a != b) return a;
        return string.CompareOrdinal(info.StartedUtc ?? string.Empty, best.StartedUtc ?? string.Empty) > 0;
    }

    public static string Name(BridgeMatch match) => match switch
    {
        BridgeMatch.Solution => "solution",
        BridgeMatch.Ancestor => "ancestor",
        BridgeMatch.WorkingDirectory => "workingDirectory",
        BridgeMatch.OnlyInstance => "onlyInstance",
        _ => null
    };

    /// <summary>Null unless the answer was not the only plausible one.</summary>
    public static string Explanation(IReadOnlyList<int> candidates, BridgeHostInfo answered, BridgeMatch match, string wanted = null)
    {
        if (match == BridgeMatch.OnlyInstance)
            return "No solution was given and none matches the working directory; used the only open Visual Studio (pid " + answered.Pid + ", " + answered.Solution + ").";

        var parts = new List<string>();
        if (match == BridgeMatch.Solution && wanted != null && !string.Equals(wanted, answered.Solution, StringComparison.OrdinalIgnoreCase))
            parts.Add("'" + wanted + "' is not itself open; answered by the Visual Studio (pid " + answered.Pid + ") that has '" + answered.Solution + "' open.");

        if (candidates.Count > 1)
            parts.Add("Several Visual Studio instances match (pids " + string.Join(", ", candidates) + "); the one that answered is pid " + answered.Pid
                + (match == BridgeMatch.Ancestor ? " (one this process was started from)" : " (this process's ancestor, else the most recently started)") + ". Unsaved edits in the others were not seen.");

        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    // Exact solution beats the same folder beats one folder containing the other (nearer is better); without a solution, the
    // deepest solution whose folder contains the working directory wins.
    public static int Score(string solution, string wanted, string cwd)
    {
        var folder = Path.GetDirectoryName(solution);
        if (wanted != null)
        {
            if (string.Equals(wanted, solution, StringComparison.OrdinalIgnoreCase)) return Exact;

            // Another solution file is another solution, however near.
            if (IsSolutionOnly(wanted)) return 0;

            var wantedFolder = IsSolutionFile(wanted) ? Path.GetDirectoryName(wanted) : wanted.TrimEnd('\\', '/');
            if (string.Equals(wantedFolder, folder, StringComparison.OrdinalIgnoreCase)) return SameFolder;

            if (Contains(wantedFolder, folder)) return Nested - Depth(folder) + Depth(wantedFolder);
            if (Contains(folder, wantedFolder)) return Nested - Depth(wantedFolder) + Depth(folder);
            return 0;
        }

        return Contains(folder, cwd) ? folder.Length : 0;
    }

    private static bool IsSolutionOnly(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSolutionFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase) || extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase);
    }

    private static bool Contains(string outer, string inner)
    {
        if (outer is null || inner is null) return false;
        var trimmedOuter = outer.TrimEnd('\\', '/');
        var trimmedInner = inner.TrimEnd('\\', '/');
        if (string.Equals(trimmedOuter, trimmedInner, StringComparison.OrdinalIgnoreCase)) return true;

        // A drive root contains everything on the drive, which says nothing about which solution is meant.
        if (trimmedOuter.Length <= 2) return false;

        return (trimmedInner + Path.DirectorySeparatorChar).StartsWith(trimmedOuter + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static int Depth(string folder) => folder.Count(c => c == '\\' || c == '/');
}
