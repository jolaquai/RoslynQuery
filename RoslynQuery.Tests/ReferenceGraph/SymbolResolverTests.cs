using System.Threading.Tasks;

using Microsoft.CodeAnalysis;

using RoslynQuery.Query;
using RoslynQuery.ReferenceGraph;

using Xunit;

namespace RoslynQuery.Tests;

// The `$$` marker in each source is the caret. Everything here is about what the caret binds to,
// so each test asserts the resolved symbol's kind and name - or that nothing resolved at all.
public class SymbolResolverTests
{
    private static async Task<ISymbol> ResolveAsync(string markedSource, string fileName = "Foo.cs")
    {
        var (source, line, column) = TestSolutions.ExtractCaret(markedSource);
        var solution = TestSolutions.Create((fileName, source));
        var active = new ActiveContext { FilePath = TestSolutions.PathFor(fileName), Line = line, Column = column };

        return await SymbolResolver.ResolveAtCaretAsync(solution, active, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Resolve_OnMethodDeclarationName_ReturnsMethod()
    {
        var symbol = await ResolveAsync("class C { void $$Target() { } }");

        Assert.Equal(SymbolKind.Method, symbol.Kind);
        Assert.Equal("Target", symbol.Name);
    }

    [Fact]
    public async Task Resolve_OnCallSite_ReturnsCalledMethod()
    {
        var symbol = await ResolveAsync("class C { void M() { $$Target(); } void Target() { } }");

        Assert.Equal(SymbolKind.Method, symbol.Kind);
        Assert.Equal("Target", symbol.Name);
    }

    [Fact]
    public async Task Resolve_OnFieldDeclaration_ReturnsField()
    {
        var symbol = await ResolveAsync("class C { int $$Count; }");

        Assert.Equal(SymbolKind.Field, symbol.Kind);
        Assert.Equal("Count", symbol.Name);
    }

    [Fact]
    public async Task Resolve_OnPropertyDeclaration_ReturnsProperty()
    {
        var symbol = await ResolveAsync("class C { int $$Value { get; set; } }");

        Assert.Equal(SymbolKind.Property, symbol.Kind);
        Assert.Equal("Value", symbol.Name);
    }

    [Fact]
    public async Task Resolve_OnEventDeclaration_ReturnsEvent()
    {
        var symbol = await ResolveAsync("using System;\r\nclass C { event Action $$Changed; }");

        Assert.Equal(SymbolKind.Event, symbol.Kind);
        Assert.Equal("Changed", symbol.Name);
    }

    [Fact]
    public async Task Resolve_OnTypeName_ReturnsNamedType()
    {
        var symbol = await ResolveAsync("class C { void M($$Other o) { } }\r\nclass Other { }");

        Assert.Equal(SymbolKind.NamedType, symbol.Kind);
        Assert.Equal("Other", symbol.Name);
    }

    [Fact]
    public async Task Resolve_OnConstructorDeclaration_ReturnsConstructor()
    {
        var symbol = await ResolveAsync("class C { public $$C() { } }");

        Assert.Equal(MethodKind.Constructor, Assert.IsAssignableFrom<IMethodSymbol>(symbol).MethodKind);
    }

    [Fact]
    public async Task Resolve_JustPastAnIdentifier_StillResolvesIt()
    {
        var symbol = await ResolveAsync("class C { void Target$$() { } }");

        Assert.Equal("Target", symbol.Name);
    }

    [Fact]
    public async Task Resolve_OnLocalDeclaration_ReturnsTheLocal()
    {
        var symbol = await ResolveAsync("class C { void M() { int $$local = 1; } }");

        Assert.Equal(SymbolKind.Local, symbol.Kind);
        Assert.Equal("local", symbol.Name);
    }

    /// <summary>The local is the answer, not the method around it.</summary>
    [Fact]
    public async Task Resolve_OnLocalUsage_ReturnsTheLocalNotTheEnclosingMethod() =>
        Assert.Equal(SymbolKind.Local, (await ResolveAsync("class C { void M() { int local = 1; local = $$local + 1; } }")).Kind);

    [Fact]
    public async Task Resolve_OnArgumentThatIsALocal_ReturnsTheLocalNotTheCall() =>
        Assert.Equal(SymbolKind.Local, (await ResolveAsync("class C { void M() { int local = 1; Take($$local); } void Take(int x) { } }")).Kind);

    [Fact]
    public async Task Resolve_OnParameterDeclaration_ReturnsTheParameter()
    {
        var symbol = await ResolveAsync("class C { void M(int $$p) { } }");

        Assert.Equal(SymbolKind.Parameter, symbol.Kind);
        Assert.Equal("p", symbol.Name);
    }

    [Fact]
    public async Task Resolve_OnLocalFunctionName_ReturnsTheLocalFunction() =>
        Assert.Equal(MethodKind.LocalFunction, Assert.IsAssignableFrom<IMethodSymbol>(
            await ResolveAsync("class C { void M() { void $$Inner() { } Inner(); } }")).MethodKind);

    [Fact]
    public async Task Resolve_OnALambdaArrow_ReturnsTheLambda() =>
        Assert.Equal(MethodKind.AnonymousFunction, Assert.IsAssignableFrom<IMethodSymbol>(
            await ResolveAsync("class C { void M() { System.Func<int, int> f = x =$$> x; } }")).MethodKind);

    [Fact]
    public async Task Resolve_OnATypeParameterDeclaration_ReturnsTheTypeParameter()
    {
        var symbol = await ResolveAsync("class C<$$T> { }");

        Assert.Equal(SymbolKind.TypeParameter, symbol.Kind);
        Assert.Equal("T", symbol.Name);
    }

    [Fact]
    public async Task Resolve_OnANamespaceDeclarationName_ReturnsTheNamespace()
    {
        var symbol = await ResolveAsync("namespace Outer.$$Inner { class C { } }");

        Assert.Equal(SymbolKind.Namespace, symbol.Kind);
        Assert.Equal("Inner", symbol.Name);
    }

    [Fact]
    public async Task Resolve_OnANamespaceInAUsingDirective_ReturnsTheNamespace()
    {
        var symbol = await ResolveAsync("using System.$$IO;\r\nclass C { }");

        Assert.Equal(SymbolKind.Namespace, symbol.Kind);
        Assert.Equal("IO", symbol.Name);
    }

    [Fact]
    public async Task Resolve_OnCrefInDocComment_ReturnsTheCrefTargetNotTheEnclosingMember()
    {
        var symbol = await ResolveAsync(
            "class C { /// <summary>See <see cref=\"$$Target\"/>.</summary>\r\n void Above() { } void Target() { } }");

        Assert.Equal(SymbolKind.Method, symbol.Kind);
        Assert.Equal("Target", symbol.Name);
    }

    [Fact]
    public async Task Resolve_WithNoActiveContext_ReturnsNull()
    {
        var solution = TestSolutions.Create(("Foo.cs", "class C { }"));

        Assert.Null(await SymbolResolver.ResolveAtCaretAsync(solution, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Resolve_WithAFilePathOutsideTheSolution_ReturnsNull()
    {
        var solution = TestSolutions.Create(("Foo.cs", "class C { }"));
        var active = new ActiveContext { FilePath = TestSolutions.PathFor("Missing.cs"), Line = 0, Column = 0 };

        Assert.Null(await SymbolResolver.ResolveAtCaretAsync(solution, active, TestContext.Current.CancellationToken));
    }
}
