using System.Linq;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;

using RoslynQuery.ReferenceGraph;

using Xunit;

namespace RoslynQuery.Tests;

// A multi-targeted project is several Roslyn projects over one set of files, so SymbolFinder reports
// every occurrence once per target framework. Left alone that turned one `new()` into one row per
// TFM, each claiming its own construction.
public class ReferenceGraphEngineLinkedFileTests
{
    private const ReferenceUsageKind All =
        ReferenceUsageKind.Invocation | ReferenceUsageKind.Read | ReferenceUsageKind.Write
        | ReferenceUsageKind.Construction | ReferenceUsageKind.TypeReference;

    private const string Source = """
        class Fmt { public Fmt() { } }

        static class Holder
        {
            static readonly Fmt Cached = new();

            static void Read() { var local = Cached; }
        }
        """;

    private static async Task<ISymbol> SymbolAsync(Solution solution, string typeName, string memberName = null)
    {
        var compilation = await solution.Projects.First().GetCompilationAsync(TestContext.Current.CancellationToken);
        var type = compilation.GetTypeByMetadataName(typeName);

        return memberName is null ? type : type.GetMembers(memberName).First();
    }

    [Fact]
    public async Task Incoming_AcrossFourTargetFrameworks_IsNotMultipliedByFour()
    {
        var solution = TestSolutions.MultiTargeted(4, "A.cs", Source);

        var nodes = await ReferenceGraphEngine.FindIncomingAsync(
            await SymbolAsync(solution, "Fmt"), solution, null, All, null, TestContext.Current.CancellationToken);

        var construction = Assert.Single(nodes, n => n.DisplayText.Contains("Cached"));

        Assert.Equal("1 construction", construction.SecondaryText);
        Assert.Single(construction.Locations);
    }

    [Fact]
    public async Task Incoming_AcrossFourTargetFrameworks_ProducesOneRowPerDeclaration()
    {
        var solution = TestSolutions.MultiTargeted(4, "A.cs", Source);

        var nodes = await ReferenceGraphEngine.FindIncomingAsync(
            await SymbolAsync(solution, "Fmt"), solution, null, All, null, TestContext.Current.CancellationToken);

        Assert.Equal(nodes.Select(n => n.DisplayText).Distinct().Count(), nodes.Count);
    }

    [Fact]
    public async Task Incoming_ForAFieldAcrossFourTargetFrameworks_CountsItsReadOnce()
    {
        var solution = TestSolutions.MultiTargeted(4, "A.cs", Source);

        var nodes = await ReferenceGraphEngine.FindIncomingAsync(
            await SymbolAsync(solution, "Holder", "Cached"), solution, null, All, null, TestContext.Current.CancellationToken);

        Assert.Equal("1 read", Assert.Single(nodes).SecondaryText);
    }

    [Fact]
    public async Task Locations_CarryTheFileAndPositionTheyCameFrom()
    {
        var solution = TestSolutions.Create(("A.cs", Source));

        var nodes = await ReferenceGraphEngine.FindIncomingAsync(
            await SymbolAsync(solution, "Holder", "Cached"), solution, null, All, null, TestContext.Current.CancellationToken);

        var location = Assert.Single(Assert.Single(nodes).Locations);

        Assert.Equal("A.cs", location.FileName);
        Assert.Equal(6, location.Line);
        Assert.Contains("A.cs (7,", location.Display);
    }
}
