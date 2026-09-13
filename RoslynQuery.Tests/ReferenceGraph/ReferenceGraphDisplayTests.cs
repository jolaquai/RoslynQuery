using System;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RoslynQuery.ReferenceGraph;

using Xunit;

namespace RoslynQuery.Tests;

public class ReferenceGraphDisplayTests
{
    private const string Source = """
        using System;
        using System.Collections.Generic;

        namespace Outer.Inner
        {
            public class Ops
            {
                public const int Limit = 4;
                public int Field;
                public List<int> Items { get; set; }
                public event EventHandler Changed;
                public Ops() { }
                public static Ops operator +(Ops left, Ops right) => left;
                public static explicit operator int(Ops value) => 0;
                public int this[int index] => index;
                public void Plain(ref int a, out long b, in decimal c, params string[] rest) { b = 0; }
                public T Generic<T>(T value, Predicate<T> match) where T : class => value;
                public Nested.Deeper MakeNested() => null;
                public class Nested { public class Deeper { } }
            }

            public enum Color { Red }

            public delegate bool Handler(int value);

            public static class Extensions
            {
                public static int Twice(this Ops ops, int factor) => 2;
            }
        }
        """;

    private static async Task<Compilation> CompilationAsync()
    {
        var solution = TestSolutions.Create(("Ops.cs", Source));

        return await solution.Projects.Single().GetCompilationAsync(TestContext.Current.CancellationToken);
    }

    private static ISymbol Find(Compilation compilation, string label)
    {
        var ops = compilation.GetTypeByMetadataName("Outer.Inner.Ops");

        switch (label)
        {
            case "class": return ops;
            case "nested class": return ops.GetTypeMembers("Nested").Single().GetTypeMembers("Deeper").Single();
            case "generic metadata type": return compilation.GetTypeByMetadataName("System.Predicate`1");
            case "metadata type": return compilation.GetTypeByMetadataName("System.IO.MemoryStream");
            case "enum": return compilation.GetTypeByMetadataName("Outer.Inner.Color");
            case "enum member": return compilation.GetTypeByMetadataName("Outer.Inner.Color").GetMembers("Red").Single();
            case "delegate": return compilation.GetTypeByMetadataName("Outer.Inner.Handler");
            case "namespace": return ops.ContainingNamespace;
            case "constructor": return ops.InstanceConstructors.Single();
            case "const field": return ops.GetMembers("Limit").Single();
            case "field": return ops.GetMembers("Field").Single();
            case "property": return ops.GetMembers("Items").Single();
            case "event": return ops.GetMembers("Changed").Single();
            case "operator": return ops.GetMembers("op_Addition").Single();
            case "conversion": return ops.GetMembers("op_Explicit").Single();
            case "indexer": return ops.GetMembers("this[]").Single();
            case "ref out in params": return ops.GetMembers("Plain").Single();
            case "generic method": return ops.GetMembers("Generic").Single();
            case "nested return type": return ops.GetMembers("MakeNested").Single();
            case "extension method": return compilation.GetTypeByMetadataName("Outer.Inner.Extensions").GetMembers("Twice").Single();
            case "metadata method":
                return compilation.GetSpecialType(SpecialType.System_String).GetMembers("Format").OfType<IMethodSymbol>()
                    .First(m => m.Parameters.Length == 2 && m.Parameters[1].Type.SpecialType == SpecialType.System_Object);
            default:
                throw new ArgumentException("Unknown label: " + label);
        }
    }

    [Theory]
    [InlineData("class", "Outer.Inner.Ops")]
    [InlineData("nested class", "Outer.Inner.Ops.Nested.Deeper")]
    [InlineData("generic metadata type", "System.Predicate<T>")]
    [InlineData("metadata type", "System.IO.MemoryStream")]
    [InlineData("enum", "Outer.Inner.Color")]
    [InlineData("enum member", "Outer.Inner.Color.Red")]
    [InlineData("delegate", "Outer.Inner.Handler")]
    [InlineData("namespace", "Outer.Inner")]
    [InlineData("constructor", "Outer.Inner.Ops.Ops()")]
    [InlineData("const field", "Outer.Inner.Ops.Limit : int")]
    [InlineData("field", "Outer.Inner.Ops.Field : int")]
    [InlineData("property", "Outer.Inner.Ops.Items : List<int>")]
    [InlineData("event", "Outer.Inner.Ops.Changed : EventHandler")]
    [InlineData("operator", "Outer.Inner.Ops.operator +(Ops, Ops) : Ops")]
    [InlineData("conversion", "Outer.Inner.Ops.explicit operator int(Ops)")]
    [InlineData("indexer", "Outer.Inner.Ops.this[int] : int")]
    [InlineData("ref out in params", "Outer.Inner.Ops.Plain(ref int, out long, in decimal, params string[]) : void")]
    [InlineData("generic method", "Outer.Inner.Ops.Generic<T>(T, Predicate<T>) : T")]
    [InlineData("nested return type", "Outer.Inner.Ops.MakeNested() : Ops.Nested.Deeper")]
    [InlineData("extension method", "Outer.Inner.Extensions.Twice(this Ops, int) : int")]
    [InlineData("metadata method", "string.Format(string, object) : string")]
    public async Task Signature_ReadsTheWayIlspySpellsIt(string label, string expected)
    {
        var compilation = await CompilationAsync();

        var parts = ReferenceGraphDisplay.SignatureOf(Find(compilation, label));

        Assert.Equal(expected, string.Concat(parts.Select(p => p.Text)));
    }

    private const string NestedSource = """
        namespace Outer.Inner
        {
            public class Generic<TItem>
            {
                public TItem Pick<TOther>(TItem candidate, ref int count)
                {
                    var running = count;
                    int Helper(int seed) => seed + running;
                    System.Func<int, int> lambda = x => x + Helper(x);
                    return candidate;
                }
            }
        }
        """;

    private static async Task<ISymbol> NestedAsync(string label)
    {
        var document = TestSolutions.Create(("Nested.cs", NestedSource)).Projects.Single().Documents.Single();
        var cancellationToken = TestContext.Current.CancellationToken;
        var model = await document.GetSemanticModelAsync(cancellationToken);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var lambda = root.DescendantNodes().OfType<SimpleLambdaExpressionSyntax>().Single();

        switch (label)
        {
            case "local": return model.GetDeclaredSymbol(root.DescendantNodes().OfType<VariableDeclaratorSyntax>().First(v => v.Identifier.Text == "running"), cancellationToken);
            case "parameter": return model.GetDeclaredSymbol(root.DescendantNodes().OfType<ParameterSyntax>().First(p => p.Identifier.Text == "count"), cancellationToken);
            case "method type parameter": return model.GetDeclaredSymbol(root.DescendantNodes().OfType<TypeParameterSyntax>().First(p => p.Identifier.Text == "TOther"), cancellationToken);
            case "type type parameter": return model.GetDeclaredSymbol(root.DescendantNodes().OfType<TypeParameterSyntax>().First(p => p.Identifier.Text == "TItem"), cancellationToken);
            case "local function": return model.GetDeclaredSymbol(root.DescendantNodes().OfType<LocalFunctionStatementSyntax>().Single(), cancellationToken);
            case "lambda": return model.GetSymbolInfo(lambda, cancellationToken).Symbol;
            case "lambda parameter": return model.GetDeclaredSymbol(lambda.Parameter, cancellationToken);
            default: throw new ArgumentException("Unknown label: " + label);
        }
    }

    [Theory]
    [InlineData("local", "Outer.Inner.Generic<TItem>.Pick.running : int")]
    [InlineData("parameter", "Outer.Inner.Generic<TItem>.Pick.count : int")]
    [InlineData("method type parameter", "Outer.Inner.Generic<TItem>.Pick.TOther")]
    [InlineData("type type parameter", "Outer.Inner.Generic<TItem>.TItem")]
    [InlineData("local function", "Outer.Inner.Generic<TItem>.Pick.Helper(int) : int")]
    [InlineData("lambda", "Outer.Inner.Generic<TItem>.Pick.lambda(int) : int")]
    [InlineData("lambda parameter", "Outer.Inner.Generic<TItem>.Pick.lambda.x : int")]
    public async Task Signature_QualifiesALocalDeclarationByTheMembersAroundIt(string label, string expected)
    {
        var parts = ReferenceGraphDisplay.SignatureOf(await NestedAsync(label));

        Assert.Equal(expected, string.Concat(parts.Select(p => p.Text)));
    }

    [Fact]
    public async Task Signature_ClassifiesContainerMemberAndReturnTypeSeparately()
    {
        var compilation = await CompilationAsync();

        var parts = ReferenceGraphDisplay.SignatureOf(Find(compilation, "ref out in params"));

        Assert.Equal(SymbolDisplayPartKind.NamespaceName, parts.First(p => p.Text == "Outer").Kind);
        Assert.Equal(SymbolDisplayPartKind.ClassName, parts.First(p => p.Text == "Ops").Kind);
        Assert.Equal(SymbolDisplayPartKind.MethodName, parts.Single(p => p.Text == "Plain").Kind);
        Assert.Equal(SymbolDisplayPartKind.Keyword, parts.Last().Kind);
    }

    [Fact]
    public void Signature_OfNull_IsEmpty() => Assert.Empty(ReferenceGraphDisplay.SignatureOf(null));

    [Fact]
    public async Task ShortLabel_KeepsItsSpelling()
    {
        var compilation = await CompilationAsync();

        Assert.Equal("Ops.Field", ReferenceGraphDisplay.Of(Find(compilation, "field")));
        Assert.Equal("Ops.Plain(ref int, out long, in decimal, params string[])", ReferenceGraphDisplay.Of(Find(compilation, "ref out in params")));
    }

    [Fact]
    public async Task ASymbolRow_RendersTheSignatureItWasBuiltWith()
    {
        var compilation = await CompilationAsync();
        var field = Find(compilation, "field");
        var signature = ReferenceGraphDisplay.SignatureOf(field);

        var node = ReferenceGraphNode.CreateSymbol(
            ReferenceGraphDisplay.Of(field), default, SymbolGlyphs.For(field), [], signature: signature);

        Assert.Same(signature, node.Signature);
    }

    [Fact]
    public void ANonSymbolRow_RendersItsDisplayTextAsASinglePlainRun()
    {
        var branch = ReferenceGraphNode.CreateAnalyzer(
            ReferenceAnalyzerKind.UsedBy, ReferenceGraphNode.CreateSymbol("row", default, SymbolGlyph.Method, []));

        var part = Assert.Single(branch.Signature);

        Assert.Equal("Used By", part.Text);
        Assert.Equal(SymbolDisplayPartKind.Text, part.Kind);
    }
}
