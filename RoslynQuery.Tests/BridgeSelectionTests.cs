using System.Collections.Generic;

using RoslynQuery.Protocol;

using Xunit;

namespace RoslynQuery.Tests;

public class BridgeSelectionTests
{
    private const string Sln = @"C:\src\App\App.slnx";

    private static BridgeHostInfo Host(int pid, string solution, string started) => new() { Pid = pid, Pipe = "p", Solution = solution, StartedUtc = started };

    private static BridgeHostInfo Pick(IReadOnlyList<BridgeHostInfo> hosts, string wanted, string cwd, ISet<int> ancestors, out List<int> candidates)
        => BridgeSelection.Pick(hosts, wanted, cwd, ancestors, out candidates);

    [Fact]
    public void Pick_SameSolutionInTwoInstances_ChoosesNewestAndReportsBoth()
    {
        var hosts = new[] { Host(1, Sln, "2026-01-01T10:00:00Z"), Host(2, Sln, "2026-01-01T11:00:00Z") };

        var picked = Pick(hosts, Sln, @"C:\elsewhere", new HashSet<int>(), out var candidates);

        Assert.Equal(2, picked.Pid);
        Assert.Equal([1, 2], candidates);
    }

    [Fact]
    public void Pick_AncestorBeatsNewer()
    {
        var hosts = new[] { Host(1, Sln, "2026-01-01T10:00:00Z"), Host(2, Sln, "2026-01-01T11:00:00Z") };

        var picked = Pick(hosts, Sln, @"C:\elsewhere", new HashSet<int> { 1 }, out var candidates);

        Assert.Equal(1, picked.Pid);
        Assert.Equal(2, candidates.Count);
    }

    [Fact]
    public void Pick_SingleMatch_HasOneCandidate()
    {
        var picked = Pick([Host(1, Sln, "a"), Host(2, @"C:\other\O.slnx", "b")], Sln, @"C:\elsewhere", new HashSet<int>(), out var candidates);

        Assert.Equal(1, picked.Pid);
        Assert.Equal([1], candidates);
    }

    [Fact]
    public void Pick_NoMatch_ReturnsNullAndNoCandidates()
    {
        var picked = Pick([Host(1, @"C:\other\O.slnx", "a")], Sln, @"C:\elsewhere", new HashSet<int>(), out var candidates);

        Assert.Null(picked);
        Assert.Empty(candidates);
    }

    [Fact]
    public void Pick_ExactSolutionBeatsSameFolderSibling_AndSiblingIsNotACandidate()
    {
        var hosts = new[] { Host(1, @"C:\src\App\Other.slnx", "2026-01-01T12:00:00Z"), Host(2, Sln, "2026-01-01T10:00:00Z") };

        var picked = Pick(hosts, Sln, @"C:\elsewhere", new HashSet<int>(), out var candidates);

        Assert.Equal(2, picked.Pid);
        Assert.Equal([2], candidates);
    }

    [Fact]
    public void Pick_NoSolutionGiven_MatchesByWorkingDirectoryBelowTheSolution()
    {
        var picked = Pick([Host(1, Sln, "a"), Host(2, @"C:\other\O.slnx", "b")], null, @"C:\src\App\sub\dir", new HashSet<int>(), out _);

        Assert.Equal(1, picked.Pid);
    }

    [Fact]
    public void AmbiguityWarning_OneCandidate_IsNull() => Assert.Null(BridgeSelection.AmbiguityWarning([7], 7));

    [Fact]
    public void AmbiguityWarning_SeveralCandidates_NamesThemAndTheAnswerer()
    {
        var warning = BridgeSelection.AmbiguityWarning([1, 2], 2);

        Assert.Contains("pids 1, 2", warning);
        Assert.Contains("pid 2", warning);
    }
}
