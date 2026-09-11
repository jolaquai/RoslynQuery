using System.Linq;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;

using RoslynQuery.ReferenceGraph;

using Xunit;

namespace RoslynQuery.Tests;

// The hierarchy analyzers search referenced assemblies, so framework symbols reach the tree whether or
// not the tool ever asked for them. They have to stay usable, and honest about what they cannot answer.
public class MetadataRowTests
{
    private const string Source = """
        using System.IO;

        namespace N
        {
            public class MyStream : Stream
            {
                public override bool CanRead => true;
                public override bool CanSeek => false;
                public override bool CanWrite => false;
                public override long Length => 0;
                public override long Position { get; set; }
                public override void Flush() { }
                public override int Read(byte[] buffer, int offset, int count) => 0;
                public override long Seek(long offset, SeekOrigin origin) => 0;
                public override void SetLength(long value) { }
                public override void Write(byte[] buffer, int offset, int count) { }
            }

            public class Implicit { }
        }
        """;

    private static async Task<(Solution Solution, Compilation Compilation)> FixtureAsync()
    {
        var solution = TestSolutions.Create(("Streams.cs", Source));
        var compilation = await solution.Projects.Single().GetCompilationAsync(TestContext.Current.CancellationToken);

        return (solution, compilation);
    }

    private static IMethodSymbol StreamRead(Compilation compilation) =>
        compilation.GetTypeByMetadataName("System.IO.Stream")
            .GetMembers("Read").OfType<IMethodSymbol>().First(m => m.Parameters.Length == 3);

    [Fact]
    public async Task IsFromMetadata_SeparatesFrameworkSymbolsFromSourceOnes()
    {
        var (_, compilation) = await FixtureAsync();

        Assert.True(SymbolIdentity.IsMetadataSymbol(compilation.GetTypeByMetadataName("System.IO.Stream")));
        Assert.True(SymbolIdentity.IsMetadataSymbol(StreamRead(compilation)));
        Assert.False(SymbolIdentity.IsMetadataSymbol(compilation.GetTypeByMetadataName("N.MyStream")));
    }

    /// <summary>An implicit constructor has no declaring syntax but is still source, so the check keys on Locations.</summary>
    [Fact]
    public async Task IsFromMetadata_IsFalseForAnImplicitlyDeclaredSourceSymbol()
    {
        var (_, compilation) = await FixtureAsync();
        var constructor = compilation.GetTypeByMetadataName("N.Implicit").InstanceConstructors.Single();

        Assert.Empty(constructor.DeclaringSyntaxReferences);
        Assert.False(SymbolIdentity.IsMetadataSymbol(constructor));
    }

    [Fact]
    public async Task AMetadataSymbolsIdentityResolvesThroughTheProjectThatReachedIt()
    {
        var (solution, compilation) = await FixtureAsync();
        var projectId = solution.ProjectIds.Single();

        var identity = SymbolIdentity.Create(StreamRead(compilation), solution, projectId);

        Assert.False(identity.IsEmpty);
        Assert.True(identity.IsFromMetadata);
        Assert.Equal(projectId, identity.ProjectId);

        var resolved = await identity.ResolveAsync(solution, TestContext.Current.CancellationToken);

        Assert.True(SymbolEqualityComparer.Default.Equals(StreamRead(compilation), resolved));
    }

    [Fact]
    public async Task For_OnAMetadataMember_OmitsUses()
    {
        var (_, compilation) = await FixtureAsync();

        var kinds = ReferenceAnalyzers.For(StreamRead(compilation));

        Assert.DoesNotContain(ReferenceAnalyzerKind.Uses, kinds);
        Assert.Contains(ReferenceAnalyzerKind.UsedBy, kinds);
        Assert.Contains(ReferenceAnalyzerKind.OverriddenBy, kinds);
    }

    [Fact]
    public async Task For_OnAMetadataType_KeepsEverythingButUses()
    {
        var (_, compilation) = await FixtureAsync();

        var kinds = ReferenceAnalyzers.For(compilation.GetTypeByMetadataName("System.IO.Stream"));

        Assert.DoesNotContain(ReferenceAnalyzerKind.Uses, kinds);
        Assert.Contains(ReferenceAnalyzerKind.DerivedTypes, kinds);
        Assert.Contains(ReferenceAnalyzerKind.ExtensionMethods, kinds);
        Assert.Contains(ReferenceAnalyzerKind.ExposedBy, kinds);
    }

    [Fact]
    public async Task For_OnAnEquivalentSourceMember_StillOffersUses()
    {
        var (_, compilation) = await FixtureAsync();
        var sourceRead = compilation.GetTypeByMetadataName("N.MyStream").GetMembers("Read").Single();

        Assert.Contains(ReferenceAnalyzerKind.Uses, ReferenceAnalyzers.For(sourceRead));
    }

    [Fact]
    public async Task AHierarchyRowFromMetadata_IsMarkedAndHasNoSourceLocation()
    {
        var (solution, compilation) = await FixtureAsync();

        var nodes = await HierarchyAnalyzers.FindDerivedTypesAsync(
            compilation.GetTypeByMetadataName("System.IO.Stream"), solution, null, null,
            TestContext.Current.CancellationToken);

        var fromMetadata = Assert.Single(nodes, n => n.DisplayText == "MemoryStream");

        Assert.True(fromMetadata.IsFromMetadata);
        Assert.Null(fromMetadata.DocumentId);
    }

    [Fact]
    public async Task ASourceRowAlongsideMetadataRows_IsNotMarked()
    {
        var (solution, compilation) = await FixtureAsync();

        var nodes = await HierarchyAnalyzers.FindDerivedTypesAsync(
            compilation.GetTypeByMetadataName("System.IO.Stream"), solution, null, null,
            TestContext.Current.CancellationToken);

        var fromSource = Assert.Single(nodes, n => n.DisplayText == "MyStream");

        Assert.False(fromSource.IsFromMetadata);
        Assert.NotNull(fromSource.DocumentId);
    }

    [Fact]
    public async Task AMetadataRow_StillOffersTheBranchesItCanAnswer()
    {
        var (solution, compilation) = await FixtureAsync();

        var nodes = await HierarchyAnalyzers.FindDerivedTypesAsync(
            compilation.GetTypeByMetadataName("System.IO.Stream"), solution, null, null,
            TestContext.Current.CancellationToken);

        var memoryStream = Assert.Single(nodes, n => n.DisplayText == "MemoryStream");
        var branches = memoryStream.Children.Select(c => c.Analyzer).ToList();

        Assert.DoesNotContain(ReferenceAnalyzerKind.Uses, branches);
        Assert.Contains(ReferenceAnalyzerKind.DerivedTypes, branches);
        Assert.Contains(ReferenceAnalyzerKind.UsedBy, branches);
    }

    /// <summary>Double-click on a metadata row opens its decompiled source, so its one call site needs a row of its own.</summary>
    [Fact]
    public async Task AMetadataRowWithOneCallSite_KeepsTheCallSiteReachable()
    {
        var solution = TestSolutions.Create(("Caller.cs", "class C { string M() => string.Format(\"{0}\", 1); }"));
        var compilation = await solution.Projects.Single().GetCompilationAsync(TestContext.Current.CancellationToken);
        var method = compilation.GetTypeByMetadataName("C").GetMembers("M").Single();

        var result = await ReferenceGraphEngine.RunAsync(
            ReferenceAnalyzerKind.Uses, method, solution, null, null, TestContext.Current.CancellationToken);

        var format = Assert.Single(result.Rows, r => r.DisplayText.StartsWith("string.Format"));

        Assert.True(format.IsFromMetadata);
        Assert.Single(format.Locations);
        Assert.Equal(NodeRole.Locations, format.Children[0].Role);
    }
}
