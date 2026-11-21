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
    public static BridgeHostInfo Pick(IReadOnlyList<BridgeHostInfo> hosts, string wantedSolution, string cwd, ISet<int> ancestors, out List<int> candidates, out BridgeMatch match)
    {
        candidates = [];
        match = BridgeMatch.None;

        if (wantedSolution != null)
        {
            var best = Best(hosts, h => Score(h.Solution, wantedSolution, cwd), out var score);
            if (best is null) return null;

            candidates = hosts.Where(h => Score(h.Solution, wantedSolution, cwd) == score).Select(h => h.Pid).ToList();
            match = BridgeMatch.Solution;
            return best;
        }

        var launchers = hosts.Where(h => ancestors.Contains(h.Pid)).ToList();
        if (launchers.Count > 0)
        {
            var best = Best(launchers, h => Math.Max(1, Score(h.Solution, null, cwd)), out _);
            candidates = launchers.Select(h => h.Pid).ToList();
            match = BridgeMatch.Ancestor;
            return best;
        }

        var byDirectory = Best(hosts, h => Score(h.Solution, null, cwd), out var directoryScore);
        if (byDirectory != null)
        {
            candidates = hosts.Where(h => Score(h.Solution, null, cwd) == directoryScore).Select(h => h.Pid).ToList();
            match = BridgeMatch.WorkingDirectory;
            return byDirectory;
        }

        if (hosts.Count == 1)
        {
            candidates = [hosts[0].Pid];
            match = BridgeMatch.OnlyInstance;
            return hosts[0];
        }

        return null;
    }

    private static BridgeHostInfo Best(IReadOnlyList<BridgeHostInfo> hosts, Func<BridgeHostInfo, int> score, out int bestScore)
    {
        BridgeHostInfo best = null;
        bestScore = 0;
        foreach (var info in hosts)
        {
            var s = score(info);
            if (s == 0) continue;
            if (best is null || s > bestScore || s == bestScore && string.CompareOrdinal(info.StartedUtc ?? string.Empty, best.StartedUtc ?? string.Empty) > 0)
            {
                best = info;
                bestScore = s;
            }
        }

        return best;
    }

    /// <summary>Null unless the answer was not the only plausible one.</summary>
    public static string Explanation(IReadOnlyList<int> candidates, BridgeHostInfo answered, BridgeMatch match) => match switch
    {
        BridgeMatch.OnlyInstance => "No solution was given and none matches the working directory; used the only open Visual Studio (pid " + answered.Pid + ", " + answered.Solution + ").",
        BridgeMatch.Ancestor => null,
        _ when candidates.Count > 1 => "Several Visual Studio instances have this solution open (pids " + string.Join(", ", candidates) + "); the one that answered is pid " + answered.Pid
            + " (the most recently started). Unsaved edits in the others were not seen.",
        _ => null
    };

    // Exact solution beats the same folder beats one folder containing the other (nearer is better); without a solution, the
    // deepest solution whose folder contains the working directory wins.
    public static int Score(string solution, string wanted, string cwd)
    {
        var folder = Path.GetDirectoryName(solution);
        if (wanted != null)
        {
            if (string.Equals(wanted, solution, StringComparison.OrdinalIgnoreCase)) return Exact;

            var wantedFolder = IsSolutionFile(wanted) ? Path.GetDirectoryName(wanted) : wanted.TrimEnd('\\', '/');
            if (string.Equals(wantedFolder, folder, StringComparison.OrdinalIgnoreCase)) return SameFolder;

            if (Contains(wantedFolder, folder)) return Nested - Depth(folder) + Depth(wantedFolder);
            if (Contains(folder, wantedFolder)) return Nested - Depth(wantedFolder) + Depth(folder);
            return 0;
        }

        return Contains(folder, cwd) ? folder.Length : 0;
    }

    private static bool IsSolutionFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase) || extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase);
    }

    private static bool Contains(string outer, string inner)
    {
        if (outer is null || inner is null) return false;
        var prefix = outer.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return (inner.TrimEnd('\\', '/') + Path.DirectorySeparatorChar).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static int Depth(string folder) => folder.Count(c => c == '\\' || c == '/');
}
