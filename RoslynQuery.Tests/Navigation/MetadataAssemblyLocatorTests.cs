using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RoslynQuery.Navigation;
using RoslynQuery.ReferenceGraph;

using Xunit;

namespace RoslynQuery.Tests;

public class MetadataAssemblyLocatorTests
{
    private const string Format = "M:System.String.Format(System.String,System.Object)";

    [Fact]
    public async Task AMetadataSymbol_LocatesTheFileItsProjectReferences()
    {
        var solution = TestSolutions.Create(("A.cs", "class C { }"));
        var identity = new SymbolIdentity(solution.ProjectIds.Single(), Format, fromMetadata: true);

        var path = await MetadataAssemblyLocator.PathOfAsync(identity, solution, TestContext.Current.CancellationToken);

        Assert.Equal(InstalledAssemblies.NetFrameworkImplementation, path, ignoreCase: true);
    }

    /// <summary>In Visual Studio the project references a reference assembly; resolving it is the provider's job, not the locator's.</summary>
    [Fact]
    public async Task AReferenceAssembly_IsReportedExactlyAsTheProjectReferencesIt()
    {
        var reference = InstalledAssemblies.NetFrameworkReference("mscorlib.dll");
        Assert.SkipUnless(File.Exists(reference), ".NET Framework 4.7.2 reference assemblies are not installed.");

        var projectId = ProjectId.CreateNewId();
        var solution = new AdhocWorkspace().AddProject(ProjectInfo.Create(
            projectId, VersionStamp.Create(), "P", "P", LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: [MetadataReference.CreateFromFile(reference)])).Solution;

        var path = await MetadataAssemblyLocator.PathOfAsync(
            new SymbolIdentity(projectId, Format, fromMetadata: true), solution, TestContext.Current.CancellationToken);

        Assert.Equal(reference, path, ignoreCase: true);
    }

    [Fact]
    public async Task ASourceSymbol_HasNoMetadataFile()
    {
        var solution = TestSolutions.Create(("A.cs", "class C { }"));

        Assert.Null(await MetadataAssemblyLocator.PathOfAsync(
            new SymbolIdentity(solution.ProjectIds.Single(), "T:C"), solution, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnUnknownProject_HasNoMetadataFile()
    {
        var solution = TestSolutions.Create(("A.cs", "class C { }"));

        Assert.Null(await MetadataAssemblyLocator.PathOfAsync(
            new SymbolIdentity(ProjectId.CreateNewId(), Format, fromMetadata: true), solution, TestContext.Current.CancellationToken));
    }
}
