using System.Linq;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;

using RoslynQuery.ReferenceGraph;

using Xunit;

namespace RoslynQuery.Tests;

public class IncomingAnalyzerTests
{
    private const string Source = """
        using System;

        namespace N
        {
            public sealed class MarkerAttribute : Attribute
            {
                public MarkerAttribute() { }
                public MarkerAttribute(Type carried) { }
            }

            public class Foo { }

            [Marker]
            public class Consumer
            {
                [Marker] private int _tagged;

                public int Counter;

                public Foo Held;

                public Foo Exposed { get; set; }

                public Foo Make() => new Foo();

                public void Accept(Foo taken) { }

                public void ReadsIt() { var local = Counter; }

                public void WritesIt() { Counter = 3; }

                public void MentionsTheType()
                {
                    Foo inner = null;
                    var cast = (Foo)(object)inner;
                    var token = typeof(Foo);
                }
            }

            [Marker(typeof(Foo))]
            public class CarriesTheTypeInAnArgument { }
        }
        """;

    private static async Task<(Solution Solution, Compilation Compilation)> FixtureAsync()
    {
        var solution = TestSolutions.Create(("Consumer.cs", Source));
        var compilation = await solution.Projects.Single().GetCompilationAsync(TestContext.Current.CancellationToken);

        return (solution, compilation);
    }

    private static async Task<string[]> RunAsync(ReferenceAnalyzerKind analyzer, ISymbol target, Solution solution)
    {
        var nodes = await ReferenceGraphEngine.FindIncomingAsync(
            analyzer, target, solution, null, null, TestContext.Current.CancellationToken);

        return nodes.Select(n => n.DisplayText).ToArray();
    }

    [Fact]
    public async Task ReadBy_AndAssignedBy_SplitTheFieldsOccurrences()
    {
        var (solution, compilation) = await FixtureAsync();
        var counter = compilation.GetTypeByMetadataName("N.Consumer").GetMembers("Counter").Single();

        var readBy = await RunAsync(ReferenceAnalyzerKind.ReadBy, counter, solution);
        var assignedBy = await RunAsync(ReferenceAnalyzerKind.AssignedBy, counter, solution);

        Assert.Equal(["Consumer.ReadsIt()"], readBy);
        Assert.Equal(["Consumer.WritesIt()"], assignedBy);
    }

    [Fact]
    public async Task InstantiatedBy_FindsConstructionAndNothingElse()
    {
        var (solution, compilation) = await FixtureAsync();
        var foo = compilation.GetTypeByMetadataName("N.Foo");

        var instantiatedBy = await RunAsync(ReferenceAnalyzerKind.InstantiatedBy, foo, solution);

        Assert.Equal(["Consumer.Make()"], instantiatedBy);
    }

    [Fact]
    public async Task ExposedBy_FindsSignaturePositions()
    {
        var (solution, compilation) = await FixtureAsync();
        var foo = compilation.GetTypeByMetadataName("N.Foo");

        var exposedBy = await RunAsync(ReferenceAnalyzerKind.ExposedBy, foo, solution);

        Assert.Contains("Consumer.Accept(Foo)", exposedBy);
        Assert.Contains("Consumer.Exposed", exposedBy);
        Assert.Contains("Consumer.Make()", exposedBy);
    }

    /// <summary>A field declares its symbol on the declarator, so its own declared type used to be credited to the containing type.</summary>
    [Fact]
    public async Task ExposedBy_AttributesAFieldsTypeToTheField()
    {
        var (solution, compilation) = await FixtureAsync();
        var foo = compilation.GetTypeByMetadataName("N.Foo");

        var exposedBy = await RunAsync(ReferenceAnalyzerKind.ExposedBy, foo, solution);

        Assert.Contains("Consumer.Held", exposedBy);
        Assert.DoesNotContain("Consumer", exposedBy);
    }

    /// <summary>A cast, a typeof and a local's declared type are type mentions, not part of any signature.</summary>
    [Fact]
    public async Task ExposedBy_IgnoresTypeMentionsInsideABody()
    {
        var (solution, compilation) = await FixtureAsync();
        var foo = compilation.GetTypeByMetadataName("N.Foo");

        var exposedBy = await RunAsync(ReferenceAnalyzerKind.ExposedBy, foo, solution);

        Assert.DoesNotContain("Consumer.MentionsTheType()", exposedBy);
    }

    [Fact]
    public async Task AppliedTo_FindsEveryApplicationSite()
    {
        var (solution, compilation) = await FixtureAsync();
        var marker = compilation.GetTypeByMetadataName("N.MarkerAttribute");

        var appliedTo = await RunAsync(ReferenceAnalyzerKind.AppliedTo, marker, solution);

        Assert.Contains("Consumer", appliedTo);
        Assert.Contains("Consumer._tagged", appliedTo);
        Assert.Contains("CarriesTheTypeInAnArgument", appliedTo);
    }

    /// <summary>A type named inside an attribute's arguments is not the attribute being applied.</summary>
    [Fact]
    public async Task AppliedTo_OnANonAttributeType_FindsNothing()
    {
        var (solution, compilation) = await FixtureAsync();
        var foo = compilation.GetTypeByMetadataName("N.Foo");

        Assert.Empty(await RunAsync(ReferenceAnalyzerKind.AppliedTo, foo, solution));
    }

    [Fact]
    public async Task UsedBy_CoversInvocationsReadsAndWritesButNotTypeMentions()
    {
        var (solution, compilation) = await FixtureAsync();
        var counter = compilation.GetTypeByMetadataName("N.Consumer").GetMembers("Counter").Single();

        var usedBy = await RunAsync(ReferenceAnalyzerKind.UsedBy, counter, solution);

        Assert.Equal(["Consumer.ReadsIt()", "Consumer.WritesIt()"], usedBy);
    }

    [Fact]
    public async Task UsedBy_OnAType_DoesNotReportTypeReferences()
    {
        var (solution, compilation) = await FixtureAsync();
        var foo = compilation.GetTypeByMetadataName("N.Foo");

        // Every mention of a type is a TypeReference or a Construction, so Used By stays out of the way
        // of Exposed By and Instantiated By.
        Assert.Empty(await RunAsync(ReferenceAnalyzerKind.UsedBy, foo, solution));
    }

    [Fact]
    public async Task EachAnalyzer_ProducesRowsThatReAnalyse()
    {
        var (solution, compilation) = await FixtureAsync();
        var counter = compilation.GetTypeByMetadataName("N.Consumer").GetMembers("Counter").Single();

        var nodes = await ReferenceGraphEngine.FindIncomingAsync(
            ReferenceAnalyzerKind.ReadBy, counter, solution, null, null, TestContext.Current.CancellationToken);

        var row = Assert.Single(nodes);
        Assert.All(row.Children, c => Assert.Equal(NodeRole.Analyzer, c.Role));
        Assert.Contains(row.Children, c => c.Analyzer == ReferenceAnalyzerKind.Uses);
    }
}
