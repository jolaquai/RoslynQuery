using System.Linq;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RoslynQuery.ReferenceGraph;

using Xunit;

namespace RoslynQuery.Tests;

public class ReferenceAnalyzersTests
{
    private const string Source = """
        using System;

        namespace N
        {
            public interface IShape
            {
                double Area();
                string Name { get; }
            }

            public abstract class Base : IShape
            {
                public int Field;
                public Base() { }
                public abstract double Area();
                public virtual string Name => "base";
                public virtual void Draw() { }
                public static void Helper() { }
                public void Plainly() { }
            }

            public class Middle : Base
            {
                public override double Area() => 1;
                public sealed override void Draw() { }
            }

            public sealed class Plain { }

            public static class Helpers { }

            public sealed class MarkerAttribute : Attribute { }

            public struct Point { public int X; }

            public enum Color { Red }

            public delegate void Handler(int value);

            public class Ops
            {
                public const int Limit = 4;
                public readonly int Ready;
                public event EventHandler Changed;
                public static Ops operator +(Ops left, Ops right) => left;
                public static explicit operator int(Ops value) => 0;
                public int this[int index] => index;
                ~Ops() { }
            }

            public static class Extensions
            {
                public static int Twice(this Point point) => point.X * 2;
            }
        }
        """;

    private static async Task<Compilation> CompilationAsync()
    {
        var solution = TestSolutions.Create(("Shapes.cs", Source));

        return await solution.Projects.Single().GetCompilationAsync(TestContext.Current.CancellationToken);
    }

    private static ISymbol Member(Compilation compilation, string type, string name) =>
        compilation.GetTypeByMetadataName("N." + type).GetMembers(name).First();

    [Fact]
    public async Task StaticMethod_GetsOnlyUsesAndUsedBy()
    {
        var compilation = await CompilationAsync();

        Assert.Equal(
            [ReferenceAnalyzerKind.Uses, ReferenceAnalyzerKind.UsedBy],
            ReferenceAnalyzers.For(Member(compilation, "Base", "Helper")));
    }

    [Fact]
    public async Task PlainInstanceMethod_GetsOnlyUsesAndUsedBy()
    {
        var compilation = await CompilationAsync();

        Assert.Equal(
            [ReferenceAnalyzerKind.Uses, ReferenceAnalyzerKind.UsedBy],
            ReferenceAnalyzers.For(Member(compilation, "Base", "Plainly")));
    }

    [Fact]
    public async Task Override_GetsOverridesAndOverriddenBy()
    {
        var compilation = await CompilationAsync();
        var kinds = ReferenceAnalyzers.For(Member(compilation, "Middle", "Area"));

        Assert.Contains(ReferenceAnalyzerKind.Overrides, kinds);
        Assert.Contains(ReferenceAnalyzerKind.OverriddenBy, kinds);
    }

    [Fact]
    public async Task SealedOverride_GetsOverridesButNotOverriddenBy()
    {
        var compilation = await CompilationAsync();
        var kinds = ReferenceAnalyzers.For(Member(compilation, "Middle", "Draw"));

        Assert.Contains(ReferenceAnalyzerKind.Overrides, kinds);
        Assert.DoesNotContain(ReferenceAnalyzerKind.OverriddenBy, kinds);
    }

    [Fact]
    public async Task VirtualMethod_GetsOverriddenByWithoutOverrides()
    {
        var compilation = await CompilationAsync();
        var kinds = ReferenceAnalyzers.For(Member(compilation, "Base", "Draw"));

        Assert.Contains(ReferenceAnalyzerKind.OverriddenBy, kinds);
        Assert.DoesNotContain(ReferenceAnalyzerKind.Overrides, kinds);
    }

    [Fact]
    public async Task InterfaceMember_GetsImplementedByAndNotOverriddenBy()
    {
        var compilation = await CompilationAsync();
        var kinds = ReferenceAnalyzers.For(Member(compilation, "IShape", "Area"));

        Assert.Contains(ReferenceAnalyzerKind.ImplementedBy, kinds);
        Assert.DoesNotContain(ReferenceAnalyzerKind.OverriddenBy, kinds);
        Assert.DoesNotContain(ReferenceAnalyzerKind.Implements, kinds);
    }

    [Fact]
    public async Task ImplementingMember_GetsImplements()
    {
        var compilation = await CompilationAsync();

        Assert.Contains(ReferenceAnalyzerKind.Implements, ReferenceAnalyzers.For(Member(compilation, "Base", "Area")));
    }

    [Fact]
    public async Task ImplementingProperty_GetsImplements()
    {
        var compilation = await CompilationAsync();

        Assert.Contains(ReferenceAnalyzerKind.Implements, ReferenceAnalyzers.For(Member(compilation, "Base", "Name")));
    }

    /// <summary>The case a plain symbol-equality check misses: the implementing member is two levels up the override chain.</summary>
    [Fact]
    public async Task OverrideOfAnImplementingMember_StillGetsImplements()
    {
        var compilation = await CompilationAsync();

        Assert.Contains(ReferenceAnalyzerKind.Implements, ReferenceAnalyzers.For(Member(compilation, "Middle", "Area")));
    }

    [Fact]
    public async Task NonImplementingMember_DoesNotGetImplements()
    {
        var compilation = await CompilationAsync();

        Assert.DoesNotContain(ReferenceAnalyzerKind.Implements, ReferenceAnalyzers.For(Member(compilation, "Base", "Draw")));
    }

    [Fact]
    public async Task Constructor_GetsOnlyUsesAndUsedBy()
    {
        var compilation = await CompilationAsync();
        var constructor = compilation.GetTypeByMetadataName("N.Base").Constructors.Single();

        Assert.Equal([ReferenceAnalyzerKind.Uses, ReferenceAnalyzerKind.UsedBy], ReferenceAnalyzers.For(constructor));
    }

    [Fact]
    public async Task Field_GetsAssignedByAndReadBy()
    {
        var compilation = await CompilationAsync();

        Assert.Equal(
            [ReferenceAnalyzerKind.Uses, ReferenceAnalyzerKind.AssignedBy, ReferenceAnalyzerKind.ReadBy],
            ReferenceAnalyzers.For(Member(compilation, "Base", "Field")));
    }

    [Fact]
    public async Task AttributeClass_GetsAppliedTo()
    {
        var compilation = await CompilationAsync();

        Assert.Contains(
            ReferenceAnalyzerKind.AppliedTo,
            ReferenceAnalyzers.For(compilation.GetTypeByMetadataName("N.MarkerAttribute")));
    }

    [Fact]
    public async Task PlainClass_DoesNotGetAppliedTo()
    {
        var compilation = await CompilationAsync();

        Assert.DoesNotContain(
            ReferenceAnalyzerKind.AppliedTo,
            ReferenceAnalyzers.For(compilation.GetTypeByMetadataName("N.Plain")));
    }

    [Fact]
    public async Task Class_GetsInstantiatedByAndDerivedTypes()
    {
        var compilation = await CompilationAsync();
        var kinds = ReferenceAnalyzers.For(compilation.GetTypeByMetadataName("N.Plain"));

        Assert.Contains(ReferenceAnalyzerKind.InstantiatedBy, kinds);
        Assert.Contains(ReferenceAnalyzerKind.DerivedTypes, kinds);
        Assert.Contains(ReferenceAnalyzerKind.ExtensionMethods, kinds);
    }

    [Fact]
    public async Task StaticClass_DoesNotGetInstantiatedBy()
    {
        var compilation = await CompilationAsync();

        Assert.DoesNotContain(
            ReferenceAnalyzerKind.InstantiatedBy,
            ReferenceAnalyzers.For(compilation.GetTypeByMetadataName("N.Helpers")));
    }

    [Fact]
    public async Task Interface_GetsDerivedTypesAndImplementedByButNotInstantiatedBy()
    {
        var compilation = await CompilationAsync();
        var kinds = ReferenceAnalyzers.For(compilation.GetTypeByMetadataName("N.IShape"));

        Assert.Contains(ReferenceAnalyzerKind.DerivedTypes, kinds);
        Assert.Contains(ReferenceAnalyzerKind.ImplementedBy, kinds);
        Assert.DoesNotContain(ReferenceAnalyzerKind.InstantiatedBy, kinds);
    }

    [Fact]
    public async Task Struct_GetsInstantiatedByButNotDerivedTypes()
    {
        var compilation = await CompilationAsync();
        var kinds = ReferenceAnalyzers.For(compilation.GetTypeByMetadataName("N.Point"));

        Assert.Contains(ReferenceAnalyzerKind.InstantiatedBy, kinds);
        Assert.DoesNotContain(ReferenceAnalyzerKind.DerivedTypes, kinds);
    }

    [Fact]
    public async Task Enum_GetsNeitherInstantiatedByNorDerivedTypes()
    {
        var compilation = await CompilationAsync();
        var kinds = ReferenceAnalyzers.For(compilation.GetTypeByMetadataName("N.Color"));

        Assert.Equal(
            [
                ReferenceAnalyzerKind.Uses,
                ReferenceAnalyzerKind.UsedBy,
                ReferenceAnalyzerKind.ExposedBy,
                ReferenceAnalyzerKind.ExtensionMethods
            ],
            kinds);
    }

    [Fact]
    public async Task Delegate_GetsInstantiatedBy()
    {
        var compilation = await CompilationAsync();
        var kinds = ReferenceAnalyzers.For(compilation.GetTypeByMetadataName("N.Handler"));

        Assert.Contains(ReferenceAnalyzerKind.InstantiatedBy, kinds);
        Assert.DoesNotContain(ReferenceAnalyzerKind.DerivedTypes, kinds);
    }

    [Fact]
    public async Task Namespace_GetsUsedByAndContains()
    {
        var compilation = await CompilationAsync();
        var ns = compilation.GlobalNamespace.GetNamespaceMembers().Single(n => n.Name == "N");

        Assert.Equal([ReferenceAnalyzerKind.UsedBy, ReferenceAnalyzerKind.Contains], ReferenceAnalyzers.For(ns));
    }

    private const string LocalsSource = """
        class Holder<TItem>
        {
            int Pick(int count)
            {
                var running = count;
                int Helper(int seed) => seed + running;
                System.Func<int, int> lambda = x => x;
                return Helper(running);
            }
        }
        """;

    private static async Task<(SemanticModel Model, SyntaxNode Root)> LocalsAsync()
    {
        var document = TestSolutions.Create(("Locals.cs", LocalsSource)).Projects.Single().Documents.Single();

        return (await document.GetSemanticModelAsync(TestContext.Current.CancellationToken),
            await document.GetSyntaxRootAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LocalAndParameter_GetAssignedByAndReadBy()
    {
        var (model, root) = await LocalsAsync();
        var local = model.GetDeclaredSymbol(root.DescendantNodes().OfType<VariableDeclaratorSyntax>().First(v => v.Identifier.Text == "running"), TestContext.Current.CancellationToken);
        var parameter = model.GetDeclaredSymbol(root.DescendantNodes().OfType<ParameterSyntax>().First(p => p.Identifier.Text == "count"), TestContext.Current.CancellationToken);

        Assert.Equal([ReferenceAnalyzerKind.AssignedBy, ReferenceAnalyzerKind.ReadBy], ReferenceAnalyzers.For(local));
        Assert.Equal([ReferenceAnalyzerKind.AssignedBy, ReferenceAnalyzerKind.ReadBy], ReferenceAnalyzers.For(parameter));
    }

    [Fact]
    public async Task TypeParameter_GetsUsedBy()
    {
        var (model, root) = await LocalsAsync();
        var typeParameter = model.GetDeclaredSymbol(root.DescendantNodes().OfType<TypeParameterSyntax>().Single(), TestContext.Current.CancellationToken);

        Assert.Equal([ReferenceAnalyzerKind.UsedBy], ReferenceAnalyzers.For(typeParameter));
    }

    [Fact]
    public async Task LocalFunction_GetsUsesAndUsedBy()
    {
        var (model, root) = await LocalsAsync();
        var function = model.GetDeclaredSymbol(root.DescendantNodes().OfType<LocalFunctionStatementSyntax>().Single(), TestContext.Current.CancellationToken);

        Assert.Equal([ReferenceAnalyzerKind.Uses, ReferenceAnalyzerKind.UsedBy], ReferenceAnalyzers.For(function));
    }

    /// <summary>Nothing can refer to a lambda, so it offers Uses alone.</summary>
    [Fact]
    public async Task Lambda_GetsUsesOnly()
    {
        var (model, root) = await LocalsAsync();
        var lambda = model.GetSymbolInfo(root.DescendantNodes().OfType<SimpleLambdaExpressionSyntax>().Single(), TestContext.Current.CancellationToken).Symbol;

        Assert.Equal([ReferenceAnalyzerKind.Uses], ReferenceAnalyzers.For(lambda));
    }

    [Fact]
    public void Headers_UseIlspyWording()
    {
        Assert.Equal("Used By", ReferenceAnalyzerKind.UsedBy.Header());
        Assert.Equal("Instantiated By", ReferenceAnalyzerKind.InstantiatedBy.Header());
        Assert.Equal("Overridden By", ReferenceAnalyzerKind.OverriddenBy.Header());
        Assert.Equal("Extension Methods", ReferenceAnalyzerKind.ExtensionMethods.Header());
        Assert.Equal("Applied To", ReferenceAnalyzerKind.AppliedTo.Header());
    }

    [Fact]
    public async Task Operator_IsAnAnalyzableRootWithUsesAndUsedBy()
    {
        var compilation = await CompilationAsync();

        Assert.Equal(
            [ReferenceAnalyzerKind.Uses, ReferenceAnalyzerKind.UsedBy],
            ReferenceAnalyzers.For(Member(compilation, "Ops", "op_Addition")));
    }

    [Fact]
    public async Task ConversionOperator_IsAnAnalyzableRootWithUsesAndUsedBy()
    {
        var compilation = await CompilationAsync();

        Assert.Equal(
            [ReferenceAnalyzerKind.Uses, ReferenceAnalyzerKind.UsedBy],
            ReferenceAnalyzers.For(Member(compilation, "Ops", "op_Explicit")));
    }

    [Fact]
    public async Task Indexer_IsAnAnalyzableRoot()
    {
        var compilation = await CompilationAsync();

        Assert.Equal(
            [ReferenceAnalyzerKind.Uses, ReferenceAnalyzerKind.UsedBy],
            ReferenceAnalyzers.For(Member(compilation, "Ops", "this[]")));
    }

    [Fact]
    public async Task Event_IsAnAnalyzableRoot()
    {
        var compilation = await CompilationAsync();

        Assert.Equal(
            [ReferenceAnalyzerKind.Uses, ReferenceAnalyzerKind.UsedBy],
            ReferenceAnalyzers.For(Member(compilation, "Ops", "Changed")));
    }

    [Fact]
    public async Task ExtensionMethod_IsAnAnalyzableRoot()
    {
        var compilation = await CompilationAsync();

        Assert.Equal(
            [ReferenceAnalyzerKind.Uses, ReferenceAnalyzerKind.UsedBy],
            ReferenceAnalyzers.For(Member(compilation, "Extensions", "Twice")));
    }

    [Fact]
    public async Task Destructor_IsAnAnalyzableRoot()
    {
        var compilation = await CompilationAsync();

        Assert.NotEmpty(ReferenceAnalyzers.For(Member(compilation, "Ops", "Finalize")));
    }

    [Fact]
    public async Task ConstAndReadonlyFields_KeepTheFieldBranches()
    {
        var compilation = await CompilationAsync();

        Assert.Equal(
            [ReferenceAnalyzerKind.Uses, ReferenceAnalyzerKind.AssignedBy, ReferenceAnalyzerKind.ReadBy],
            ReferenceAnalyzers.For(Member(compilation, "Ops", "Limit")));

        Assert.Equal(
            [ReferenceAnalyzerKind.Uses, ReferenceAnalyzerKind.AssignedBy, ReferenceAnalyzerKind.ReadBy],
            ReferenceAnalyzers.For(Member(compilation, "Ops", "Ready")));
    }

    /// <summary>An enum member cannot be assigned to, so Assigned By would be a branch that is always empty.</summary>
    [Fact]
    public async Task EnumMember_DoesNotGetAssignedBy()
    {
        var compilation = await CompilationAsync();
        Assert.Equal(
            [ReferenceAnalyzerKind.Uses, ReferenceAnalyzerKind.ReadBy],
            ReferenceAnalyzers.For(Member(compilation, "Color", "Red")));
    }

    [Fact]
    public void EveryKind_HasANonEmptyHeader()
    {
        foreach (ReferenceAnalyzerKind kind in System.Enum.GetValues(typeof(ReferenceAnalyzerKind)))
            Assert.False(string.IsNullOrWhiteSpace(kind.Header()));
    }
}
