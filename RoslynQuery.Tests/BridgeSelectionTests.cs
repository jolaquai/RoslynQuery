using System.Collections.Generic;

using RoslynQuery.Protocol;

using Xunit;

namespace RoslynQuery.Tests;

public class BridgeSelectionTests
{
    private const string Sln = @"C:\src\App\App.slnx";
    private static readonly HashSet<int> None = [];

    private static BridgeHostInfo Host(int pid, string solution, string started = "2026-01-01T10:00:00Z") => new() { Pid = pid, Pipe = "p", Solution = solution, StartedUtc = started };

    [Fact]
    public void Pick_ExplicitSolutionInTwoInstances_ChoosesNewestAndReportsBoth()
    {
        var picked = BridgeSelection.Pick([Host(1, Sln), Host(2, Sln, "2026-01-01T11:00:00Z")], Sln, @"C:\elsewhere", None, out var candidates, out var match);

        Assert.Equal(2, picked.Pid);
        Assert.Equal([1, 2], candidates);
        Assert.Equal(BridgeMatch.Solution, match);
    }

    [Fact]
    public void Pick_ExplicitExactSolutionBeatsAnAncestorWithASiblingSolution()
    {
        var picked = BridgeSelection.Pick([Host(1, @"C:\src\App\Other.slnx"), Host(2, Sln)], Sln, @"C:\elsewhere", new HashSet<int> { 1 }, out var candidates, out _);

        Assert.Equal(2, picked.Pid);
        Assert.Equal([2], candidates);
    }

    [Fact]
    public void Pick_ExplicitFolderContainingTheOpenSolution_Matches()
    {
        var picked = BridgeSelection.Pick([Host(1, @"C:\some\path\lower\down\App.slnx")], @"C:\some", @"C:\elsewhere", None, out _, out var match);

        Assert.Equal(1, picked.Pid);
        Assert.Equal(BridgeMatch.Solution, match);
    }

    [Fact]
    public void Pick_ExplicitSolutionInASubfolderOfTheOpenOnes_Matches()
    {
        var picked = BridgeSelection.Pick([Host(1, Sln)], @"C:\src\App\tools\Tool.csproj", @"C:\elsewhere", None, out _, out _);

        Assert.Equal(1, picked.Pid);
    }

    [Fact]
    public void Pick_ExplicitNearerNestedSolutionBeatsFartherOne()
    {
        var picked = BridgeSelection.Pick([Host(1, @"C:\some\a\b\c\X.slnx"), Host(2, @"C:\some\a\Y.slnx", "2026-01-01T09:00:00Z")], @"C:\some", @"C:\elsewhere", None, out var candidates, out _);

        Assert.Equal(2, picked.Pid);
        Assert.Equal([2], candidates);
    }

    [Fact]
    public void Pick_ExplicitUnrelatedSolution_ReturnsNull()
    {
        var picked = BridgeSelection.Pick([Host(1, Sln)], @"D:\other\O.slnx", @"C:\src\App", new HashSet<int> { 1 }, out var candidates, out var match);

        Assert.Null(picked);
        Assert.Empty(candidates);
        Assert.Equal(BridgeMatch.None, match);
    }

    [Fact]
    public void Pick_NoSolution_AncestorWinsEvenWhenTheWorkingDirectoryIsUnrelated()
    {
        var picked = BridgeSelection.Pick([Host(1, Sln), Host(2, @"C:\src\B\B.slnx", "2026-01-01T11:00:00Z")], null, @"C:\Program Files\VS", new HashSet<int> { 1 }, out _, out var match);

        Assert.Equal(1, picked.Pid);
        Assert.Equal(BridgeMatch.Ancestor, match);
    }

    [Fact]
    public void Pick_NoSolution_WorkingDirectoryBelowTheSolutionChoosesIt()
    {
        var picked = BridgeSelection.Pick([Host(1, Sln), Host(2, @"C:\other\O.slnx")], null, @"C:\src\App\sub\dir", None, out _, out var match);

        Assert.Equal(1, picked.Pid);
        Assert.Equal(BridgeMatch.WorkingDirectory, match);
    }

    [Fact]
    public void Pick_NoSolutionNoMatch_UsesTheOnlyOpenInstance()
    {
        var picked = BridgeSelection.Pick([Host(1, Sln)], null, @"C:\Users\someone", None, out _, out var match);

        Assert.Equal(1, picked.Pid);
        Assert.Equal(BridgeMatch.OnlyInstance, match);
    }

    [Fact]
    public void Pick_NoSolutionNoMatchSeveralInstances_ReturnsNull()
    {
        var picked = BridgeSelection.Pick([Host(1, Sln), Host(2, @"C:\src\B\B.slnx")], null, @"C:\Users\someone", None, out _, out _);

        Assert.Null(picked);
    }

    [Theory]
    [InlineData("Solution", "solution")]
    [InlineData("Ancestor", "ancestor")]
    [InlineData("WorkingDirectory", "workingDirectory")]
    [InlineData("OnlyInstance", "onlyInstance")]
    [InlineData("None", null)]
    public void Name_IsTheWireValue(string match, string expected) => Assert.Equal(expected, BridgeSelection.Name((BridgeMatch)System.Enum.Parse(typeof(BridgeMatch), match)));

    [Fact]
    public void Explanation_SingleCandidate_IsNull()
    {
        var host = Host(2, Sln);

        Assert.Null(BridgeSelection.Explanation([2], host, BridgeMatch.Ancestor));
        Assert.Null(BridgeSelection.Explanation([2], host, BridgeMatch.Solution, Sln));
    }

    [Fact]
    public void Explanation_SeveralAncestors_NamesThem()
    {
        var text = BridgeSelection.Explanation([1, 2], Host(2, Sln), BridgeMatch.Ancestor);

        Assert.Contains("pids 1, 2", text);
        Assert.Contains("started from", text);
    }

    [Fact]
    public void Explanation_RequestedSolutionNotItselfOpen_SaysWhichOneAnswered()
    {
        var text = BridgeSelection.Explanation([2], Host(2, Sln), BridgeMatch.Solution, @"C:\src\App\tools\Tool.csproj");

        Assert.Contains("not itself open", text);
        Assert.Contains(Sln, text);
    }

    [Fact]
    public void Pick_ExplicitSolutionFileNeverMatchesAnotherSolutionInTheSameFolder()
    {
        var picked = BridgeSelection.Pick([Host(1, Sln)], @"C:\src\App\Other.slnx", @"C:\elsewhere", None, out var candidates, out var match);

        Assert.Null(picked);
        Assert.Empty(candidates);
        Assert.Equal(BridgeMatch.None, match);
    }

    [Fact]
    public void Pick_ExplicitTie_PrefersTheInstanceTheProcessWasStartedFromOverANewerOne()
    {
        var picked = BridgeSelection.Pick([Host(1, Sln), Host(2, Sln, "2026-01-01T11:00:00Z")], Sln, @"C:\elsewhere", new HashSet<int> { 1 }, out var candidates, out _);

        Assert.Equal(1, picked.Pid);
        Assert.Equal([1, 2], candidates);
    }

    [Fact]
    public void Pick_SolutionAtADriveRoot_DoesNotClaimEveryWorkingDirectoryOnThatDrive()
    {
        var picked = BridgeSelection.Pick([Host(1, @"C:\Root.slnx"), Host(2, @"D:\x\B.slnx")], null, @"C:\Users\someone\work", None, out _, out _);

        Assert.Null(picked);
    }

    [Fact]
    public void Pick_ExplicitDriveRoot_MatchesNoSolutionBelowIt()
    {
        var picked = BridgeSelection.Pick([Host(1, Sln)], @"C:\", @"C:\elsewhere", None, out _, out _);

        Assert.Null(picked);
    }

    [Fact]
    public void Pick_OnlyInstanceNotAllowed_ReturnsNull()
    {
        var picked = BridgeSelection.Pick([Host(1, Sln)], null, @"C:\Users\someone", None, out _, out var match, allowOnlyInstance: false);

        Assert.Null(picked);
        Assert.Equal(BridgeMatch.None, match);
    }

    [Fact]
    public void Pick_FolderWithTrailingSlashAndOtherCase_MatchesTheSolutionInside()
    {
        var picked = BridgeSelection.Pick([Host(1, Sln)], @"c:\SRC\app\", @"C:\elsewhere", None, out _, out var match);

        Assert.Equal(1, picked.Pid);
        Assert.Equal(BridgeMatch.Solution, match);
    }

    [Fact]
    public void Explanation_SeveralCandidates_NamesThemAndTheAnswerer()
    {
        var text = BridgeSelection.Explanation([1, 2], Host(2, Sln), BridgeMatch.Solution);

        Assert.Contains("pids 1, 2", text);
        Assert.Contains("pid 2", text);
    }

    [Fact]
    public void Explanation_OnlyInstance_SaysSo()
    {
        var text = BridgeSelection.Explanation([1], Host(1, Sln), BridgeMatch.OnlyInstance);

        Assert.Contains("only open Visual Studio", text);
        Assert.Contains(Sln, text);
    }
}
