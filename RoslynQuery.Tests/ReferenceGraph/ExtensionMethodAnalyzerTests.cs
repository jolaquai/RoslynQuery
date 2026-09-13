using System.Linq;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;

using RoslynQuery.ReferenceGraph;

using Xunit;

namespace RoslynQuery.Tests;

public class ExtensionMethodAnalyzerTests
{
    private const string Source = """
        namespace N
        {
            public interface IShape { }

            public class Widget : IShape { }

            public class Unrelated { }

            public struct Point { }

            public static class Extensions
            {
                public static int OnWidget(this Widget widget) => 1;
                public static int OnShape(this IShape shape) => 2;
                public static int OnUnrelated(this Unrelated other) => 3;
                public static int OnPoint(this Point point) => 4;
                public static int OnAnything<T>(this T value) => 5;
                public static int OnConstrained<T>(this T value) where T : Unrelated => 6;
            }
        }
        """;

    private static async Task<(Solution Solution, Compilation Compilation)> FixtureAsync()
    {
        var solution = TestSolutions.Create(("Widgets.cs", Source));
        var compilation = await solution.Projects.Single().GetCompilationAsync(TestContext.Current.CancellationToken);

        return (solution, compilation);
    }

    private static async Task<string[]> ForWidgetAsync()
    {
        var (solution, compilation) = await FixtureAsync();

        var nodes = await HierarchyAnalyzers.FindExtensionMethodsAsync(
            compilation.GetTypeByMetadataName("N.Widget"), solution, null, TestContext.Current.CancellationToken);

        return nodes.Select(n => n.DisplayText).ToArray();
    }

    [Fact]
    public async Task FindsAnExtensionOnTheTypeItself()
    {
        Assert.Contains(await ForWidgetAsync(), d => d.Contains("OnWidget"));
    }

    [Fact]
    public async Task FindsAnExtensionOnAnImplementedInterface()
    {
        Assert.Contains(await ForWidgetAsync(), d => d.Contains("OnShape"));
    }

    [Fact]
    public async Task FindsAnUnconstrainedGenericExtension()
    {
        Assert.Contains(await ForWidgetAsync(), d => d.Contains("OnAnything"));
    }

    [Fact]
    public async Task IgnoresAnExtensionOnAnUnrelatedType()
    {
        var found = await ForWidgetAsync();

        Assert.DoesNotContain(found, d => d.Contains("OnUnrelated"));
        Assert.DoesNotContain(found, d => d.Contains("OnPoint"));
    }

    [Fact]
    public async Task IgnoresAGenericExtensionConstrainedAwayFromTheType()
    {
        Assert.DoesNotContain(await ForWidgetAsync(), d => d.Contains("OnConstrained"));
    }

    /// <summary>The reduced form has no declaration of its own, so a reduced row could not navigate anywhere.</summary>
    [Fact]
    public async Task ReturnsTheUnreducedDefinitionSoTheRowNavigates()
    {
        var (solution, compilation) = await FixtureAsync();

        var nodes = await HierarchyAnalyzers.FindExtensionMethodsAsync(
            compilation.GetTypeByMetadataName("N.Widget"), solution, null, TestContext.Current.CancellationToken);

        var row = Assert.Single(nodes, n => n.DisplayText.Contains("OnWidget"));

        Assert.Equal("Extensions.OnWidget(Widget)", row.DisplayText);
        Assert.NotNull(row.DocumentId);
    }

    [Fact]
    public async Task OnANonType_IsEmpty()
    {
        var (solution, compilation) = await FixtureAsync();
        var member = compilation.GetTypeByMetadataName("N.Extensions").GetMembers("OnWidget").First();

        Assert.Empty(await HierarchyAnalyzers.FindExtensionMethodsAsync(
            member, solution, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnExtensionOnAnInterface_IsFoundForTheInterfaceItself()
    {
        var (solution, compilation) = await FixtureAsync();

        var nodes = await HierarchyAnalyzers.FindExtensionMethodsAsync(
            compilation.GetTypeByMetadataName("N.IShape"), solution, null, TestContext.Current.CancellationToken);

        Assert.Contains(nodes, n => n.DisplayText.Contains("OnShape"));
        Assert.DoesNotContain(nodes, n => n.DisplayText.Contains("OnWidget"));
    }
}
