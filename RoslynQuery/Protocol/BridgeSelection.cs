using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RoslynQuery.Protocol;

/// <summary>Which of several live Visual Studio bridges a client should talk to.</summary>
internal static class BridgeSelection
{
    /// <summary>
    /// Ancestry first, then how well the solution matches, then the newest instance, so ties are never arbitrary.
    /// <paramref name="candidates"/> are the pids matching the chosen one equally well (the chosen pid included).
    /// </summary>
    public static BridgeHostInfo Pick(IReadOnlyList<BridgeHostInfo> hosts, string wantedSolution, string cwd, ISet<int> ancestors, out List<int> candidates)
    {
        candidates = [];

        BridgeHostInfo best = null;
        var bestScore = 0;
        (bool Ancestor, int Score, string Started) bestKey = default;

        foreach (var info in hosts)
        {
            var score = Score(info.Solution, wantedSolution, cwd);
            if (score == 0) continue;

            var key = (ancestors.Contains(info.Pid), score, info.StartedUtc ?? string.Empty);
            if (best is null || key.CompareTo(bestKey) > 0) { best = info; bestKey = key; bestScore = score; }
        }

        if (best != null)
            candidates = hosts.Where(h => Score(h.Solution, wantedSolution, cwd) == bestScore).Select(h => h.Pid).ToList();

        return best;
    }

    /// <summary>Null unless more than one instance matched equally well.</summary>
    public static string AmbiguityWarning(IReadOnlyList<int> candidates, int answered)
        => candidates.Count < 2
            ? null
            : "Several Visual Studio instances have this solution open (pids " + string.Join(", ", candidates) + "); the one that answered is pid " + answered
                + " (an ancestor of this process if there is one, else the most recently started). Unsaved edits in the others were not seen.";

    // Exact solution beats "same folder" beats "cwd is somewhere below the solution"; the deepest such solution wins.
    public static int Score(string solution, string wanted, string cwd)
    {
        var folder = Path.GetDirectoryName(solution);
        if (wanted != null)
        {
            if (string.Equals(wanted, solution, StringComparison.OrdinalIgnoreCase)) return int.MaxValue;
            var wantedFolder = Path.GetDirectoryName(wanted);
            return string.Equals(wantedFolder, folder, StringComparison.OrdinalIgnoreCase) ? int.MaxValue - 1 : 0;
        }

        var prefix = folder.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return (cwd + Path.DirectorySeparatorChar).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? folder.Length : 0;
    }
}
