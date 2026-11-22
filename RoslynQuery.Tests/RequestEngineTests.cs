using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

using RoslynQuery.Protocol;
using RoslynQuery.Query;

using Xunit;

namespace RoslynQuery.Tests;

[Collection(PredicateCompilerCacheCollection.Name)]
public class RequestEngineTests
{
    private const string Path = @"C:\src\A.cs";

    private static Func1 OpenWith(string source, string projectName = "P") => _ =>
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject(ProjectInfo.Create(
            ProjectId.CreateNewId(), VersionStamp.Create(), projectName, projectName, LanguageNames.CSharp,
            filePath: @"C:\src\P.csproj",
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]));
        workspace.AddDocument(DocumentInfo.Create(
            DocumentId.CreateNewId(project.Id), "A.cs",
            loader: TextLoader.From(TextAndVersion.Create(SourceText.From(source), VersionStamp.Create())),
            filePath: Path));

        return Task.FromResult(new Session { Workspace = workspace, Solution = workspace.CurrentSolution, Root = @"C:\src", Source = "test" });
    };

    private delegate Task<Session> Func1(CancellationToken cancellationToken);

    private static RunRequest Replace(string query = "t.IsKind(SyntaxKind.IdentifierToken) && t.Text == \"x\"", bool apply = false, string expect = null) => new()
    {
        Query = query,
        Replace = "\"renamed\"",
        Target = TargetKind.SyntaxToken,
        Scope = ScopeKind.Solution,
        Apply = apply,
        ExpectPlan = expect
    };

    private static Task<RunResponse> RunAsync(RunRequest request, Func1 open, List<ApplyContext> applied) =>
        RequestEngine.RunAsync(request, ct => open(ct), context => { applied.Add(context); return Task.CompletedTask; }, CancellationToken.None);

    [Fact]
    public async Task Apply_WithoutExpectPlan_IsRefusedAndWritesNothing()
    {
        var applied = new List<ApplyContext>();

        var response = await RunAsync(Replace(apply: true), OpenWith("class C { int x; }"), applied);

        Assert.False(response.Ok);
        Assert.Contains("ExpectPlan", response.Error);
        Assert.Empty(applied);
    }

    [Fact]
    public async Task Apply_WithThePlanIdOfTheDryRun_Writes()
    {
        var open = OpenWith("class C { int x; }");
        var dry = await RunAsync(Replace(), open, []);
        var applied = new List<ApplyContext>();

        var response = await RunAsync(Replace(apply: true, expect: dry.PlanId), open, applied);

        Assert.True(response.Applied);
        Assert.Single(applied);
        Assert.Contains(Path, applied[0].Files.Keys);
        Assert.Contains("renamed", applied[0].Files[Path].New.ToString());
    }

    [Fact]
    public async Task Apply_WithAnotherPlanId_Exits3AndWritesNothing()
    {
        var applied = new List<ApplyContext>();

        var response = await RunAsync(Replace(apply: true, expect: "0000000000000000"), OpenWith("class C { int x; }"), applied);

        Assert.Equal(3, response.ExitCode);
        Assert.Empty(applied);
    }

    [Fact]
    public async Task Apply_ThatNowPlansNothingButExpectedSomething_Exits3()
    {
        var applied = new List<ApplyContext>();

        var response = await RunAsync(Replace("t.Text == \"nothing\"", apply: true, expect: "1234567890abcdef"), OpenWith("class C { int x; }"), applied);

        Assert.Equal(3, response.ExitCode);
        Assert.False(response.Applied);
    }

    [Fact]
    public async Task Apply_HandsTheReviewedPlanToTheHost_AndTheOldTextIsWhatWasPlannedFrom()
    {
        var open = OpenWith("class C { int x; }");
        var dry = await RunAsync(Replace(), open, []);
        var applied = new List<ApplyContext>();

        await RunAsync(Replace(apply: true, expect: dry.PlanId), open, applied);

        Assert.NotNull(applied[0].Plan);
        Assert.Equal("class C { int x; }", applied[0].Files[Path].Old.ToString());
        Assert.Equal(applied[0].Plan.BaseTexts.Values.Single().ToString(), applied[0].Files[Path].Old.ToString());
    }

    [Fact]
    public async Task ACancelledRequest_NeverPlansOrApplies()
    {
        var applied = new List<ApplyContext>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<System.OperationCanceledException>(() =>
            RequestEngine.RunAsync(Replace(apply: true, expect: "x"), ct => OpenWith("class C { int x; }")(ct), context => { applied.Add(context); return Task.CompletedTask; }, cts.Token));

        Assert.Empty(applied);
    }

    [Fact]
    public async Task Apply_WhoseHostFailsPartWay_ReportsFailureNotARefusal()
    {
        var open = OpenWith("class C { int x; }");
        var dry = await RunAsync(Replace(), open, []);

        var response = await RequestEngine.RunAsync(Replace(apply: true, expect: dry.PlanId), ct => open(ct), _ => throw new ApplyFailedException("half written"), CancellationToken.None);

        Assert.False(response.Ok);
        Assert.Equal(1, response.ExitCode);
        Assert.Contains("half written", response.Error);
    }

    [Fact]
    public async Task ProjectScope_MatchesAMultiTargetedProjectByItsFileName()
    {
        var request = new RunRequest { Query = "t.Text == \"x\"", Target = TargetKind.SyntaxToken, Scope = ScopeKind.Project, Project = "P" };

        var response = await RunAsync(request, OpenWith("class C { int x; }", "P (net8.0)"), []);

        Assert.True(response.Ok, response.Error);
        Assert.Single(response.Hits);
    }
}

public class ReleaseConsistencyTests
{
    private static string Repo()
    {
        var directory = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (directory != null && !File.Exists(System.IO.Path.Combine(directory.FullName, "RoslynQuery", "RoslynQuery.csproj"))) directory = directory.Parent;
        return directory?.FullName;
    }

    [Fact]
    public void TheVersionShownInVisualStudio_IsTheProjectVersion()
    {
        var repo = Repo();
        Assert.NotNull(repo);

        var project = File.ReadAllText(System.IO.Path.Combine(repo, "RoslynQuery", "RoslynQuery.csproj"));
        var package = File.ReadAllText(System.IO.Path.Combine(repo, "RoslynQuery", "RoslynQueryPackage.cs"));

        var version = Regex.Match(project, "<Version>([^<]+)</Version>").Groups[1].Value;
        var shown = Regex.Match(package, "InstalledProductRegistration\\(\"#110\", \"#112\", \"([^\"]+)\"\\)").Groups[1].Value;

        Assert.NotEmpty(version);
        Assert.Equal(version, shown);
    }
}
