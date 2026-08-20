using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;

using RoslynQuery.ReferenceGraph;

using Xunit;

namespace RoslynQuery.Tests;

public class ReferenceGraphEngineIncomingTests
{
    private const ReferenceUsageKind All =
        ReferenceUsageKind.Invocation | ReferenceUsageKind.Read | ReferenceUsageKind.Write
        | ReferenceUsageKind.Construction | ReferenceUsageKind.TypeReference;

    private static async Task<ISymbol> SymbolAsync(Solution solution, string typeName, string memberName = null)
    {
        var compilation = await solution.Projects.Single().GetCompilationAsync(TestContext.Current.CancellationToken);
        var type = compilation.GetTypeByMetadataName(typeName);

        return memberName is null ? type : type.GetMembers(memberName).First();
    }

    [Fact]
    public async Task Incoming_AMethodCalledFromTwoMethods_ProducesTwoInvocationNodes()
    {
        var solution = TestSolutions.Create(
            ("A.cs", "class Owner { public void Target() { } public void CallerInA() { Target(); } }"),
            ("B.cs", "class Other { void CallerInB() { new Owner().Target(); } }"));

        var nodes = await ReferenceGraphEngine.FindIncomingAsync(
            await SymbolAsync(solution, "Owner", "Target"), solution, null, All, null, TestContext.Current.CancellationToken);

        Assert.Equal(2, nodes.Count);
        Assert.All(nodes, n => Assert.Equal(ReferenceUsageKind.Invocation, Assert.Single(n.Locations).Kind));
        Assert.Contains(nodes, n => n.DisplayText.Contains("CallerInA"));
        Assert.Contains(nodes, n => n.DisplayText.Contains("CallerInB"));
    }

    [Fact]
    public async Task Incoming_AFieldReadAndWritten_FlagsEachCallerSeparately()
    {
        var solution = TestSolutions.Create(
            ("Holder.cs", "class Holder { public int Count; public int Read() { return Count; } public void Write() { Count = 1; } }"));

        var nodes = await ReferenceGraphEngine.FindIncomingAsync(
            await SymbolAsync(solution, "Holder", "Count"), solution, null, All, null, TestContext.Current.CancellationToken);

        Assert.Equal(ReferenceUsageKind.Read, Assert.Single(nodes.Single(n => n.DisplayText.Contains("Read")).Locations).Kind);
        Assert.Equal(ReferenceUsageKind.Write, Assert.Single(nodes.Single(n => n.DisplayText.Contains("Write")).Locations).Kind);
    }

    [Fact]
    public async Task Incoming_RestrictedToOneDocument_ExcludesTheCallerInTheOtherFile()
    {
        var solution = TestSolutions.Create(
            ("A.cs", "class Owner { public void Target() { } public void CallerInA() { Target(); } }"),
            ("B.cs", "class Other { void CallerInB() { new Owner().Target(); } }"));

        var target = await SymbolAsync(solution, "Owner", "Target");
        var justA = ImmutableHashSet.Create(TestSolutions.Document(solution, "A.cs"));

        var nodes = await ReferenceGraphEngine.FindIncomingAsync(
            target, solution, justA, All, null, TestContext.Current.CancellationToken);

        Assert.Contains("CallerInA", Assert.Single(nodes).DisplayText);
    }

    [Fact]
    public async Task Incoming_ATypeRoot_SeesBothConstructionAndTypeUsage()
    {
        var solution = TestSolutions.Create(
            ("Foo.cs", "class Foo { }\r\nclass Uses { public void Create() { var f = new Foo(); } public void Accept(Foo f) { } }"));

        var nodes = await ReferenceGraphEngine.FindIncomingAsync(
            await SymbolAsync(solution, "Foo"), solution, null, All, null, TestContext.Current.CancellationToken);

        Assert.Equal(ReferenceUsageKind.Construction, Assert.Single(nodes.Single(n => n.DisplayText.Contains("Create")).Locations).Kind);
        Assert.Equal(ReferenceUsageKind.TypeReference, Assert.Single(nodes.Single(n => n.DisplayText.Contains("Accept")).Locations).Kind);
    }

    [Fact]
    public async Task Incoming_WithTypeReferenceFilteredOut_DropsTheTypeUsageNode()
    {
        var solution = TestSolutions.Create(
            ("Foo.cs", "class Foo { }\r\nclass Uses { public void Create() { var f = new Foo(); } public void Accept(Foo f) { } }"));

        var nodes = await ReferenceGraphEngine.FindIncomingAsync(
            await SymbolAsync(solution, "Foo"), solution, null, ReferenceUsageKind.Construction, null, TestContext.Current.CancellationToken);

        Assert.Contains("Create", Assert.Single(nodes).DisplayText);
    }

    [Fact]
    public async Task Incoming_AReferenceInsideALambda_IsAttributedToTheEnclosingMethod()
    {
        var solution = TestSolutions.Create(
            ("A.cs", """
                using System;
                class Owner
                {
                    public void Target() { }
                    public void Caller() { Action a = () => Target(); a(); }
                }
                """));

        var nodes = await ReferenceGraphEngine.FindIncomingAsync(
            await SymbolAsync(solution, "Owner", "Target"), solution, null, All, null, TestContext.Current.CancellationToken);

        Assert.Contains("Caller", Assert.Single(nodes).DisplayText);
    }

    [Fact]
    public async Task Incoming_AReferenceInsideAnAccessor_IsAttributedToTheProperty()
    {
        var solution = TestSolutions.Create(
            ("A.cs", "class Owner { public int Backing; public int Wrapper { get { return Backing; } } }"));

        var nodes = await ReferenceGraphEngine.FindIncomingAsync(
            await SymbolAsync(solution, "Owner", "Backing"), solution, null, All, null, TestContext.Current.CancellationToken);

        var node = Assert.Single(nodes);
        Assert.Contains("Wrapper", node.DisplayText);
        Assert.Equal(SymbolGlyph.Property, node.Glyph);
    }

    [Fact]
    public async Task Incoming_MoreCallersThanTheCap_CollapsesTheRemainderIntoOneRow()
    {
        var source = new StringBuilder("class Owner { public void Target() { } }\r\n");
        for (var i = 0; i < MaxPlusFive; i++)
            source.Append($"class Caller{i} {{ void Go() {{ new Owner().Target(); }} }}\r\n");

        var solution = TestSolutions.Create(("Many.cs", source.ToString()));

        var nodes = await ReferenceGraphEngine.FindIncomingAsync(
            await SymbolAsync(solution, "Owner", "Target"), solution, null, All, null, TestContext.Current.CancellationToken);

        Assert.Equal(ReferenceGraphEngine.MaxNodes + 1, nodes.Count);
        Assert.Equal("5 more...", nodes[ReferenceGraphEngine.MaxNodes].DisplayText);
        Assert.True(nodes[ReferenceGraphEngine.MaxNodes].IsMessage);
    }

    private const int MaxPlusFive = ReferenceGraphEngine.MaxNodes + 5;

    [Fact]
    public async Task Incoming_WithNoReferences_ProducesNoNodes()
    {
        var solution = TestSolutions.Create(("A.cs", "class Owner { public void Lonely() { } }"));

        var nodes = await ReferenceGraphEngine.FindIncomingAsync(
            await SymbolAsync(solution, "Owner", "Lonely"), solution, null, All, null, TestContext.Current.CancellationToken);

        Assert.Empty(nodes);
    }

    // SymbolFinder searches documents in parallel, so grouping by first-seen order made the row
    // order change from one refresh to the next over an unchanged solution.
    [Fact]
    public async Task Incoming_RowOrder_IsSortedAndStableAcrossRuns()
    {
        var solution = TestSolutions.Create(
            ("A.cs", "class Owner { public void Target() { } }"),
            ("Z.cs", "class Zeta { void Go() { new Owner().Target(); } }"),
            ("M.cs", "class Mid { void Go() { new Owner().Target(); } }"),
            ("B.cs", "class Beta { void Go() { new Owner().Target(); } }"),
            ("Q.cs", "class Quux { void Go() { new Owner().Target(); } }"));

        var target = await SymbolAsync(solution, "Owner", "Target");

        var first = await ReferenceGraphEngine.FindIncomingAsync(
            target, solution, null, All, null, TestContext.Current.CancellationToken);
        var second = await ReferenceGraphEngine.FindIncomingAsync(
            target, solution, null, All, null, TestContext.Current.CancellationToken);

        var names = first.Select(n => n.DisplayText).ToList();

        Assert.Equal(["Beta.Go()", "Mid.Go()", "Quux.Go()", "Zeta.Go()"], names);
        Assert.Equal(names, second.Select(n => n.DisplayText));
    }

    [Fact]
    public async Task Incoming_LocationsWithinARow_AreOrderedSoDoubleClickIsStable()
    {
        var solution = TestSolutions.Create(
            ("A.cs", """
                class Owner
                {
                    public void Target() { }
                    public void Caller() { Target(); Target(); Target(); }
                }
                """));

        var target = await SymbolAsync(solution, "Owner", "Target");

        var first = await ReferenceGraphEngine.FindIncomingAsync(
            target, solution, null, All, null, TestContext.Current.CancellationToken);
        var second = await ReferenceGraphEngine.FindIncomingAsync(
            target, solution, null, All, null, TestContext.Current.CancellationToken);

        var spans = Assert.Single(first).Locations.Select(l => l.Span.Start).ToList();

        Assert.Equal(spans.OrderBy(x => x), spans);
        Assert.Equal(spans, Assert.Single(second).Locations.Select(l => l.Span.Start));
    }

    [Fact]
    public async Task Incoming_TheCap_KeepsTheFirstRowsInSortedOrder()
    {
        var source = new StringBuilder();
        source.AppendLine("class Owner { public void Target() { } }");
        for (var i = 0; i < MaxPlusFive; i++)
            source.AppendLine($"class Caller{i:D4} {{ void Go() {{ new Owner().Target(); }} }}");

        var solution = TestSolutions.Create(("Many.cs", source.ToString()));

        var nodes = await ReferenceGraphEngine.FindIncomingAsync(
            await SymbolAsync(solution, "Owner", "Target"), solution, null, All, null, TestContext.Current.CancellationToken);

        var rows = nodes.Take(ReferenceGraphEngine.MaxNodes).Select(n => n.DisplayText).ToList();

        // Which rows survive the cap must not depend on which document finished searching first.
        Assert.Equal(rows.OrderBy(x => x, System.StringComparer.Ordinal), rows);
        Assert.Equal("Caller0000.Go()", rows[0]);
    }
}
