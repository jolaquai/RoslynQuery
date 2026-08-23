using System.Linq;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;

using RoslynQuery.ReferenceGraph;

using Xunit;

namespace RoslynQuery.Tests;

public class ReferenceGraphEngineOutgoingTests
{
    private const ReferenceUsageKind All =
        ReferenceUsageKind.Invocation | ReferenceUsageKind.Read | ReferenceUsageKind.Write
        | ReferenceUsageKind.Construction | ReferenceUsageKind.TypeReference;

    private const ReferenceUsageKind NoTypes = All & ~ReferenceUsageKind.TypeReference;

    private static async Task<ISymbol> SymbolAsync(Solution solution, string typeName, string memberName = null)
    {
        var compilation = await solution.Projects.Single().GetCompilationAsync(TestContext.Current.CancellationToken);
        var type = compilation.GetTypeByMetadataName(typeName);

        return memberName is null ? type : type.GetMembers(memberName).First();
    }

    private static ReferenceGraphNode RootNodeFor(ISymbol symbol, Solution solution) =>
        new ReferenceGraphNode(
            symbol.Name,
            SymbolIdentity.Create(symbol, solution, solution.Projects.Single().Id),
            SymbolGlyphs.For(symbol),
            ReferenceDirection.Outgoing);

    [Fact]
    public async Task Outgoing_AMethodCallingTwoMethods_ProducesTwoInvocationNodes()
    {
        var solution = TestSolutions.Create(
            ("A.cs", "class C { void Root() { First(); Second(); } void First() { } void Second() { } }"));

        var nodes = await ReferenceGraphEngine.FindOutgoingAsync(
            await SymbolAsync(solution, "C", "Root"), solution, NoTypes, null, TestContext.Current.CancellationToken);

        Assert.Equal(2, nodes.Count);
        Assert.All(nodes, n => Assert.Equal(ReferenceUsageKind.Invocation, Assert.Single(n.Locations).Kind));
        Assert.Contains(nodes, n => n.DisplayText.Contains("First"));
        Assert.Contains(nodes, n => n.DisplayText.Contains("Second"));
    }

    [Fact]
    public async Task Outgoing_AMethodReadingOneFieldAndWritingAnother_FlagsThemSeparately()
    {
        var solution = TestSolutions.Create(
            ("A.cs", "class C { int Source; int Sink; void Root() { Sink = Source; } }"));

        var nodes = await ReferenceGraphEngine.FindOutgoingAsync(
            await SymbolAsync(solution, "C", "Root"), solution, NoTypes, null, TestContext.Current.CancellationToken);

        Assert.Equal(ReferenceUsageKind.Read, Assert.Single(nodes.Single(n => n.DisplayText.Contains("Source")).Locations).Kind);
        Assert.Equal(ReferenceUsageKind.Write, Assert.Single(nodes.Single(n => n.DisplayText.Contains("Sink")).Locations).Kind);
    }

    [Fact]
    public async Task Outgoing_ASelfRecursiveMethod_FlagsItselfAndStopsThere()
    {
        var solution = TestSolutions.Create(
            ("A.cs", "class C { void Root(int n) { if (n > 0) Root(n - 1); } }"));

        var root = await SymbolAsync(solution, "C", "Root");
        var parent = RootNodeFor(root, solution);

        var nodes = await ReferenceGraphEngine.FindOutgoingAsync(
            root, solution, NoTypes, parent, TestContext.Current.CancellationToken);

        var self = Assert.Single(nodes);
        Assert.Contains("Root", self.DisplayText);
        Assert.True(self.IsRecursive);
        Assert.False(self.IsExpandable);
        Assert.Empty(self.Children);
    }

    [Fact]
    public async Task Outgoing_APartialMethod_UnionsBothDeclarations()
    {
        var solution = TestSolutions.Create(
            ("A.cs", "partial class C { partial void Root(); void FromA() { } void FromB() { } }"),
            ("B.cs", "partial class C { partial void Root() { FromA(); FromB(); } }"));

        var nodes = await ReferenceGraphEngine.FindOutgoingAsync(
            await SymbolAsync(solution, "C", "Root"), solution, NoTypes, null, TestContext.Current.CancellationToken);

        Assert.Equal(2, nodes.Count);
        Assert.Contains(nodes, n => n.DisplayText.Contains("FromA"));
        Assert.Contains(nodes, n => n.DisplayText.Contains("FromB"));
    }

    [Fact]
    public async Task Outgoing_ATypeRoot_IncludesItsBaseTypeAndItsMembersReferences()
    {
        var solution = TestSolutions.Create(
            ("A.cs",
                "class Base { public void Inherited() { } }\r\n"
                + "class Helper { public static void Assist() { } }\r\n"
                + "class Root : Base { void OnlyHere() { Helper.Assist(); } }"));

        var nodes = await ReferenceGraphEngine.FindOutgoingAsync(
            await SymbolAsync(solution, "Root"), solution, All, null, TestContext.Current.CancellationToken);

        Assert.Contains(nodes, n => n.DisplayText.Contains("Base") && n.Locations[0].Kind == ReferenceUsageKind.TypeReference);
        Assert.Contains(nodes, n => n.DisplayText.Contains("Assist") && n.Locations[0].Kind == ReferenceUsageKind.Invocation);
    }

    [Fact]
    public async Task Outgoing_ATypeRoot_DoesNotDescendIntoANestedType()
    {
        var solution = TestSolutions.Create(
            ("A.cs",
                "class Helper { public static void Assist() { } }\r\n"
                + "class Root { class Nested { void Inner() { Helper.Assist(); } } }"));

        var nodes = await ReferenceGraphEngine.FindOutgoingAsync(
            await SymbolAsync(solution, "Root"), solution, All, null, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(nodes, n => n.DisplayText.Contains("Assist"));
    }

    [Fact]
    public async Task Outgoing_ObjectCreation_IsReportedAsTheConstructor()
    {
        var solution = TestSolutions.Create(
            ("A.cs", "class Made { public Made(int x) { } }\r\nclass C { void Root() { var m = new Made(1); } }"));

        var nodes = await ReferenceGraphEngine.FindOutgoingAsync(
            await SymbolAsync(solution, "C", "Root"), solution, All, null, TestContext.Current.CancellationToken);

        var node = Assert.Single(nodes);
        Assert.Equal(ReferenceUsageKind.Construction, Assert.Single(node.Locations).Kind);
        Assert.Equal(SymbolGlyph.Constructor, node.Glyph);
    }

    [Fact]
    public async Task Outgoing_AParameterType_IsATypeReferenceThatTheFilterCanRemove()
    {
        var solution = TestSolutions.Create(
            ("A.cs", "class Other { }\r\nclass C { void Root(Other o) { } }"));

        var root = await SymbolAsync(solution, "C", "Root");

        var withTypes = await ReferenceGraphEngine.FindOutgoingAsync(
            root, solution, All, null, TestContext.Current.CancellationToken);
        var withoutTypes = await ReferenceGraphEngine.FindOutgoingAsync(
            root, solution, NoTypes, null, TestContext.Current.CancellationToken);

        Assert.Equal(ReferenceUsageKind.TypeReference, Assert.Single(Assert.Single(withTypes).Locations).Kind);
        Assert.Empty(withoutTypes);
    }

    [Fact]
    public async Task Outgoing_AFieldRoot_ReportsItsDeclaredType()
    {
        var solution = TestSolutions.Create(
            ("A.cs", "class Other { }\r\nclass C { Other Field = null; }"));

        var nodes = await ReferenceGraphEngine.FindOutgoingAsync(
            await SymbolAsync(solution, "C", "Field"), solution, All, null, TestContext.Current.CancellationToken);

        Assert.Contains(nodes, n => n.DisplayText.Contains("Other") && n.Locations[0].Kind == ReferenceUsageKind.TypeReference);
    }

    [Fact]
    public async Task Outgoing_AFieldRoot_ReportsItsInitializersReferences()
    {
        var solution = TestSolutions.Create(
            ("A.cs", "class Other { public static int Seed; }\r\nclass C { int Copy = Other.Seed; }"));

        var nodes = await ReferenceGraphEngine.FindOutgoingAsync(
            await SymbolAsync(solution, "C", "Copy"), solution, NoTypes, null, TestContext.Current.CancellationToken);

        Assert.Contains(nodes, n => n.DisplayText.Contains("Seed") && n.Locations[0].Kind == ReferenceUsageKind.Read);
    }

    [Fact]
    public async Task Outgoing_LocalsAndParameters_AreNotRows()
    {
        var solution = TestSolutions.Create(
            ("A.cs", "class C { void Root(int p) { int local = p; local++; } }"));

        var nodes = await ReferenceGraphEngine.FindOutgoingAsync(
            await SymbolAsync(solution, "C", "Root"), solution, All, null, TestContext.Current.CancellationToken);

        Assert.Empty(nodes);
    }

    [Fact]
    public async Task Outgoing_ACallInsideALambda_StillCounts()
    {
        var solution = TestSolutions.Create(
            ("A.cs",
                "using System;\r\n"
                + "class C { void Root() { Action a = () => Helper(); a(); } void Helper() { } }"));

        var nodes = await ReferenceGraphEngine.FindOutgoingAsync(
            await SymbolAsync(solution, "C", "Root"), solution, NoTypes, null, TestContext.Current.CancellationToken);

        Assert.Contains(nodes, n => n.DisplayText.Contains("Helper"));
    }

    [Fact]
    public async Task Outgoing_TheSameTargetTwice_IsOneNodeWithTwoLocations()
    {
        var solution = TestSolutions.Create(
            ("A.cs", "class C { void Root() { Helper(); Helper(); } void Helper() { } }"));

        var nodes = await ReferenceGraphEngine.FindOutgoingAsync(
            await SymbolAsync(solution, "C", "Root"), solution, NoTypes, null, TestContext.Current.CancellationToken);

        var node = Assert.Single(nodes);
        Assert.Equal(2, node.Locations.Count);
        Assert.Equal("2 invocations", node.SecondaryText);
    }

    [Fact]
    public async Task Outgoing_AConstructorChainingToAnother_IsAConstructionNode()
    {
        var solution = TestSolutions.Create(
            ("A.cs", "class C { public C() { } public C(int x) : this() { } }"));

        var compilation = await solution.Projects.Single().GetCompilationAsync(TestContext.Current.CancellationToken);
        var chaining = compilation.GetTypeByMetadataName("C").InstanceConstructors.Single(c => c.Parameters.Length == 1);

        var nodes = await ReferenceGraphEngine.FindOutgoingAsync(
            chaining, solution, All, null, TestContext.Current.CancellationToken);

        Assert.Equal(ReferenceUsageKind.Construction, Assert.Single(Assert.Single(nodes).Locations).Kind);
    }
}
