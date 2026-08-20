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
    public async Task Resolve_OnLocalDeclaration_ReturnsNull() =>
        Assert.Null(await ResolveAsync("class C { void M() { int $$local = 1; } }"));

    [Fact]
    public async Task Resolve_OnLocalUsage_DoesNotFallBackToEnclosingMethod() =>
        Assert.Null(await ResolveAsync("class C { void M() { int local = 1; local = $$local + 1; } }"));

    [Fact]
    public async Task Resolve_OnArgumentThatIsALocal_DoesNotDriftToTheCall() =>
        Assert.Null(await ResolveAsync("class C { void M() { int local = 1; Take($$local); } void Take(int x) { } }"));

    [Fact]
    public async Task Resolve_OnParameterDeclaration_ReturnsNull() =>
        Assert.Null(await ResolveAsync("class C { void M(int $$p) { } }"));

    [Fact]
    public async Task Resolve_OnLocalFunctionName_ReturnsNull() =>
        Assert.Null(await ResolveAsync("class C { void M() { void $$Inner() { } Inner(); } }"));

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
