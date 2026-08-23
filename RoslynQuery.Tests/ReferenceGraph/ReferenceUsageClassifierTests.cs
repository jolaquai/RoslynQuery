using System.Linq;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

using RoslynQuery.ReferenceGraph;

using Xunit;

namespace RoslynQuery.Tests;

// One source fixture drives every case: each test names the method to look inside and the
// identifier to classify, so the interesting part of a test is one line.
public class ReferenceUsageClassifierTests
{
    private const string Source = """
        using System;
        using System.Collections.Generic;

        class Foo
        {
            public int Value;
            public event Action Changed;

            public Foo() { }
            public Foo(int v) : this() { Value = v; }

            public void Invoke() { Helper(); }
            public void Helper() { }

            public int ReadField() { return Value; }
            public void WriteField() { Value = 1; }
            public void CompoundField() { Value += 1; }
            public void IncrementField() { Value++; }
            public void Subscribe(Action a) { Changed += a; }
            public void Construct() { var f = new Foo(); }
            public void Cast(object o) { var f = (Foo)o; }
            public void TypeOf() { var t = typeof(Foo); }
            public void Generic() { var l = new List<Foo>(); }
            public void Catch() { try { } catch (InvalidOperationException e) { } }
            public void Parameter(Foo other) { }
            public void OutArg() { TakeOut(out Value); }
            public void RefArg() { TakeRef(ref Value); }
            public void TakeOut(out int x) { x = 0; }
            public void TakeRef(ref int x) { }
            public void MethodGroup() { Action a = Helper; }
            public void ObjectInitializer() { var f = new Foo { Value = 2 }; }
            public void Qualified(Foo other) { other.Value = 3; }

            /// <summary>See <see cref="Helper"/>.</summary>
            public void Documented() { }
        }

        class Bar : Foo { }
        """;

    private static async Task<(SemanticModel Model, SyntaxNode Root)> CompileAsync()
    {
        var workspace = new AdhocWorkspace();
        var projectInfo = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Create(),
            "ClassifierTestProject",
            "ClassifierTestProject",
            LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);

        var project = workspace.AddProject(projectInfo);
        var document = workspace.AddDocument(project.Id, "Foo.cs", SourceText.From(Source));

        var token = TestContext.Current.CancellationToken;
        return (await document.GetSemanticModelAsync(token), await document.GetSyntaxRootAsync(token));
    }

    private static async Task<ReferenceUsageKind> ClassifyAsync(string inMethod, string identifier)
    {
        var (model, root) = await CompileAsync();

        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().First(m => m.Identifier.Text == inMethod);
        var name = method.DescendantNodes().OfType<SimpleNameSyntax>().First(n => n.Identifier.Text == identifier);

        return ReferenceUsageClassifier.Classify(name, model.GetSymbolInfo(name, TestContext.Current.CancellationToken).Symbol);
    }

    [Fact]
    public async Task Classify_PlainInvocation_IsInvocation() =>
        Assert.Equal(ReferenceUsageKind.Invocation, await ClassifyAsync("Invoke", "Helper"));

    [Fact]
    public async Task Classify_FieldRead_IsRead() =>
        Assert.Equal(ReferenceUsageKind.Read, await ClassifyAsync("ReadField", "Value"));

    [Fact]
    public async Task Classify_AssignmentLeftHandSide_IsWrite() =>
        Assert.Equal(ReferenceUsageKind.Write, await ClassifyAsync("WriteField", "Value"));

    [Fact]
    public async Task Classify_CompoundAssignment_IsReadAndWrite() =>
        Assert.Equal(ReferenceUsageKind.Read | ReferenceUsageKind.Write, await ClassifyAsync("CompoundField", "Value"));

    [Fact]
    public async Task Classify_Increment_IsWrite() =>
        Assert.Equal(ReferenceUsageKind.Write, await ClassifyAsync("IncrementField", "Value"));

    [Fact]
    public async Task Classify_EventSubscription_IsWrite() =>
        Assert.Equal(ReferenceUsageKind.Write, await ClassifyAsync("Subscribe", "Changed"));

    [Fact]
    public async Task Classify_ObjectCreation_IsConstruction() =>
        Assert.Equal(ReferenceUsageKind.Construction, await ClassifyAsync("Construct", "Foo"));

    [Fact]
    public async Task Classify_Cast_IsTypeReference() =>
        Assert.Equal(ReferenceUsageKind.TypeReference, await ClassifyAsync("Cast", "Foo"));

    [Fact]
    public async Task Classify_TypeOf_IsTypeReference() =>
        Assert.Equal(ReferenceUsageKind.TypeReference, await ClassifyAsync("TypeOf", "Foo"));

    [Fact]
    public async Task Classify_GenericTypeArgument_IsTypeReference() =>
        Assert.Equal(ReferenceUsageKind.TypeReference, await ClassifyAsync("Generic", "Foo"));

    [Fact]
    public async Task Classify_CatchClauseType_IsTypeReference() =>
        Assert.Equal(ReferenceUsageKind.TypeReference, await ClassifyAsync("Catch", "InvalidOperationException"));

    [Fact]
    public async Task Classify_ParameterType_IsTypeReference() =>
        Assert.Equal(ReferenceUsageKind.TypeReference, await ClassifyAsync("Parameter", "Foo"));

    [Fact]
    public async Task Classify_OutArgument_IsWrite() =>
        Assert.Equal(ReferenceUsageKind.Write, await ClassifyAsync("OutArg", "Value"));

    [Fact]
    public async Task Classify_RefArgument_IsReadAndWrite() =>
        Assert.Equal(ReferenceUsageKind.Read | ReferenceUsageKind.Write, await ClassifyAsync("RefArg", "Value"));

    [Fact]
    public async Task Classify_MethodGroup_IsRead() =>
        Assert.Equal(ReferenceUsageKind.Read, await ClassifyAsync("MethodGroup", "Helper"));

    [Fact]
    public async Task Classify_ObjectInitializerMember_IsWrite() =>
        Assert.Equal(ReferenceUsageKind.Write, await ClassifyAsync("ObjectInitializer", "Value"));

    [Fact]
    public async Task Classify_QualifiedWriteThroughMemberAccess_IsWrite() =>
        Assert.Equal(ReferenceUsageKind.Write, await ClassifyAsync("Qualified", "Value"));

    [Fact]
    public async Task Classify_ConstructorInitializer_IsConstruction()
    {
        var (model, root) = await CompileAsync();

        var initializer = root.DescendantNodes().OfType<ConstructorInitializerSyntax>().Single();

        Assert.Equal(ReferenceUsageKind.Construction, ReferenceUsageClassifier.Classify(initializer, model.GetSymbolInfo(initializer, TestContext.Current.CancellationToken).Symbol));
    }

    [Fact]
    public async Task Classify_BaseListEntry_IsTypeReference()
    {
        var (model, root) = await CompileAsync();

        var baseType = root.DescendantNodes().OfType<SimpleBaseTypeSyntax>().Single().Type;

        Assert.Equal(ReferenceUsageKind.TypeReference, ReferenceUsageClassifier.Classify(baseType, model.GetSymbolInfo(baseType, TestContext.Current.CancellationToken).Symbol));
    }

    [Fact]
    public async Task Classify_CrefInDocComment_IsDocumentation()
    {
        var (model, root) = await CompileAsync();

        var cref = root.DescendantNodes(descendIntoTrivia: true).OfType<NameMemberCrefSyntax>().Single();

        Assert.Equal(ReferenceUsageKind.Documentation, ReferenceUsageClassifier.Classify(cref.Name, model.GetSymbolInfo(cref, TestContext.Current.CancellationToken).Symbol));
    }
}
