using System.Linq;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;

using RoslynQuery.ReferenceGraph;

using Xunit;

namespace RoslynQuery.Tests;

/// <summary>
/// Hiding metadata consumers of metadata symbols: a branch that lists what depends on a framework symbol keeps the
/// dependents in the solution and drops the ones from referenced assemblies. What a symbol builds on stays whole.
/// </summary>
public class MetadataConsumerTests
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
        }
        """;

    private static async Task<(Solution Solution, Compilation Compilation)> FixtureAsync()
    {
        var solution = TestSolutions.Create(("Streams.cs", Source));
        var compilation = await solution.Projects.Single().GetCompilationAsync(TestContext.Current.CancellationToken);

        return (solution, compilation);
    }

    private static IMethodSymbol ReadOf(INamedTypeSymbol type) =>
        type.GetMembers("Read").OfType<IMethodSymbol>().First(m => m.Parameters.Length == 3);

    [Theory]
    [InlineData((int)ReferenceAnalyzerKind.UsedBy, true)]
    [InlineData((int)ReferenceAnalyzerKind.ReadBy, true)]
    [InlineData((int)ReferenceAnalyzerKind.AssignedBy, true)]
    [InlineData((int)ReferenceAnalyzerKind.InstantiatedBy, true)]
    [InlineData((int)ReferenceAnalyzerKind.ExposedBy, true)]
    [InlineData((int)ReferenceAnalyzerKind.AppliedTo, true)]
    [InlineData((int)ReferenceAnalyzerKind.OverriddenBy, true)]
    [InlineData((int)ReferenceAnalyzerKind.ImplementedBy, true)]
    [InlineData((int)ReferenceAnalyzerKind.DerivedTypes, true)]
    [InlineData((int)ReferenceAnalyzerKind.ExtensionMethods, true)]
    [InlineData((int)ReferenceAnalyzerKind.Uses, false)]
    [InlineData((int)ReferenceAnalyzerKind.Overrides, false)]
    [InlineData((int)ReferenceAnalyzerKind.Implements, false)]
    [InlineData((int)ReferenceAnalyzerKind.Contains, false)]
    public void EveryAnalyzer_IsClassifiedAsListingConsumersOrNot(int analyzer, bool consumers) =>
        Assert.Equal(consumers, ReferenceAnalyzers.ListsConsumers((ReferenceAnalyzerKind)analyzer));

    [Fact]
    public void EveryAnalyzerKind_IsCoveredByTheClassification() =>
        Assert.Equal(14, System.Enum.GetValues(typeof(ReferenceAnalyzerKind)).Length);

    [Fact]
    public async Task Hidden_AMetadataTypesDerivedTypesKeepOnlyTheSolutionsOwn()
    {
        var (solution, compilation) = await FixtureAsync();

        var result = await ReferenceGraphEngine.RunAsync(
            ReferenceAnalyzerKind.DerivedTypes, compilation.GetTypeByMetadataName("System.IO.Stream"), solution, null, null,
            showMetadataConsumers: false, TestContext.Current.CancellationToken);

        Assert.Contains(result.Rows, r => r.DisplayText == "MyStream");
        Assert.DoesNotContain(result.Rows, r => r.IsFromMetadata);
    }

    [Fact]
    public async Task Shown_AMetadataTypesDerivedTypesIncludeTheFrameworksToo()
    {
        var (solution, compilation) = await FixtureAsync();

        var result = await ReferenceGraphEngine.RunAsync(
            ReferenceAnalyzerKind.DerivedTypes, compilation.GetTypeByMetadataName("System.IO.Stream"), solution, null, null,
            showMetadataConsumers: true, TestContext.Current.CancellationToken);

        Assert.Contains(result.Rows, r => r.DisplayText == "MyStream");
        Assert.Contains(result.Rows, r => r.DisplayText == "MemoryStream" && r.IsFromMetadata);
    }

    /// <summary>The overload without the switch is what everything outside the tool window calls, and it hides nothing.</summary>
    [Fact]
    public async Task TheOverloadWithoutTheSwitch_ShowsEverything()
    {
        var (solution, compilation) = await FixtureAsync();

        var result = await ReferenceGraphEngine.RunAsync(
            ReferenceAnalyzerKind.DerivedTypes, compilation.GetTypeByMetadataName("System.IO.Stream"), solution, null, null,
            TestContext.Current.CancellationToken);

        Assert.Contains(result.Rows, r => r.DisplayText == "MemoryStream" && r.IsFromMetadata);
    }

    /// <summary>What a framework symbol builds on is not a consumer, so it survives even with consumers hidden.</summary>
    [Fact]
    public async Task Hidden_AMetadataMembersOverridesStillShowItsMetadataBase()
    {
        var (solution, compilation) = await FixtureAsync();
        var memoryStreamRead = ReadOf(compilation.GetTypeByMetadataName("System.IO.MemoryStream"));

        var result = await ReferenceGraphEngine.RunAsync(
            ReferenceAnalyzerKind.Overrides, memoryStreamRead, solution, null, null,
            showMetadataConsumers: false, TestContext.Current.CancellationToken);

        Assert.Contains(result.Rows, r => r.IsFromMetadata && r.DisplayText.StartsWith("Stream.Read"));
    }

    /// <summary>Only a metadata symbol's consumers are filtered; a source symbol's branches are left exactly as found.</summary>
    [Fact]
    public async Task Hidden_ASourceMembersOverridesStillShowTheFrameworkBase()
    {
        var (solution, compilation) = await FixtureAsync();
        var myRead = ReadOf(compilation.GetTypeByMetadataName("N.MyStream"));

        var result = await ReferenceGraphEngine.RunAsync(
            ReferenceAnalyzerKind.Overrides, myRead, solution, null, null,
            showMetadataConsumers: false, TestContext.Current.CancellationToken);

        Assert.Contains(result.Rows, r => r.IsFromMetadata && r.DisplayText.StartsWith("Stream.Read"));
    }
}
