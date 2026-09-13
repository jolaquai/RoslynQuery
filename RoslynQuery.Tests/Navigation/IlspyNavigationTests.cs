using System;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;

using RoslynQuery.Navigation;

using Xunit;

namespace RoslynQuery.Tests;

/// <summary>
/// What ILSpy 11 needs to select a symbol it was launched with, measured against it: the file it is handed must define
/// the type, since a forwarder in a facade resolves only to the forwarder and selects nothing; and a method id must not
/// carry a <c>~ReturnType</c> suffix, which ILSpy matches only on conversion operators.
/// </summary>
public class IlspyNavigationTests
{
    private static string SharedRuntime()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"dotnet\shared\Microsoft.NETCore.App");
        if (!Directory.Exists(root)) return null;

        return Directory.EnumerateDirectories(root)
            .Where(d => File.Exists(Path.Combine(d, "System.Private.CoreLib.dll")) && File.Exists(Path.Combine(d, "System.Runtime.dll")))
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .LastOrDefault();
    }

    private static bool Defines(string path, string ns, string name)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();

        return reader.TypeDefinitions
            .Select(reader.GetTypeDefinition)
            .Any(t => reader.StringComparer.Equals(t.Name, name) && reader.StringComparer.Equals(t.Namespace, ns));
    }

    [Theory]
    [InlineData("System.Runtime.dll", "T:System.IDisposable", "System", "IDisposable")]
    [InlineData("System.Threading.dll", "M:System.Threading.Interlocked.Exchange(System.Int32@,System.Int32)~System.Int32", "System.Threading", "Interlocked")]
    [InlineData("System.Runtime.dll", "T:System.Environment.SpecialFolder", "System", "Environment")]
    [InlineData("System.Runtime.dll", "P:System.String.Length", "System", "String")]
    public void AForwardedType_LeadsToTheFileThatDefinesIt(string facade, string documentationId, string ns, string name)
    {
        var shared = SharedRuntime();
        Assert.SkipUnless(shared != null, "No .NET shared runtime is installed.");

        var start = Path.Combine(shared, facade);
        Assert.SkipUnless(File.Exists(start), facade + " is not in " + shared + ".");
        Assert.False(Defines(start, ns, name), "the starting file has to be a facade for this to test anything");

        var declaring = ImplementationAssemblyResolver.DeclaringAssembly(start, documentationId);

        Assert.Equal("System.Private.CoreLib.dll", Path.GetFileName(declaring), ignoreCase: true);
        Assert.True(Defines(declaring, ns, name));
    }

    [Fact]
    public void ATypeDefinedInTheFileItself_StaysInThatFile()
    {
        var shared = SharedRuntime();
        Assert.SkipUnless(shared != null, "No .NET shared runtime is installed.");

        var corelib = Path.Combine(shared, "System.Private.CoreLib.dll");

        Assert.Equal(corelib, ImplementationAssemblyResolver.DeclaringAssembly(corelib, "T:System.String"));
    }

    [Theory]
    [InlineData("T:No.Such.Type")]
    [InlineData("N:System")]
    [InlineData("not an id")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingItCannotFollow_LeavesThePathAlone(string documentationId)
    {
        var shared = SharedRuntime();
        Assert.SkipUnless(shared != null, "No .NET shared runtime is installed.");

        var facade = Path.Combine(shared, "System.Runtime.dll");

        Assert.Equal(facade, ImplementationAssemblyResolver.DeclaringAssembly(facade, documentationId));
    }

    [Theory]
    [InlineData("M:System.Threading.Interlocked.Exchange(System.Int32@,System.Int32)~System.Int32", "M:System.Threading.Interlocked.Exchange(System.Int32@,System.Int32)")]
    [InlineData("M:N.C.Count~System.Int32", "M:N.C.Count")]
    [InlineData("M:N.C.M``1(``0)~``0", "M:N.C.M``1(``0)")]
    [InlineData("M:N.C.op_Implicit(N.C)~System.Int32", "M:N.C.op_Implicit(N.C)~System.Int32")]
    [InlineData("M:N.C.op_Explicit(N.C)~System.Int64", "M:N.C.op_Explicit(N.C)~System.Int64")]
    [InlineData("M:N.C.Dispose", "M:N.C.Dispose")]
    [InlineData("T:System.IDisposable", "T:System.IDisposable")]
    [InlineData("P:System.String.Length", "P:System.String.Length")]
    [InlineData("F:N.C.Field", "F:N.C.Field")]
    [InlineData("E:N.C.Changed", "E:N.C.Changed")]
    [InlineData("N:System", "N:System")]
    [InlineData(null, null)]
    public void TheNavigationId_DropsTheReturnSuffixExceptOnConversions(string documentationId, string expected) =>
        Assert.Equal(expected, IlspyLauncher.NavigationId(documentationId));

    [Fact]
    public void TheResponseFile_CarriesTheNavigationId() =>
        Assert.Contains(
            "--navigateto:M:System.Threading.Interlocked.Exchange(System.Int32@,System.Int32)\r\n",
            IlspyLauncher.ResponseFileText(@"C:\a.dll", "M:System.Threading.Interlocked.Exchange(System.Int32@,System.Int32)~System.Int32"));

    /// <summary>Runs the ids Roslyn really produces through the normalization, so a change in Roslyn's format cannot slip past.</summary>
    [Fact]
    public async Task RoslynsOwnIds_ComeOutInTheFormIlspyMatches()
    {
        const string source = """
            namespace N
            {
                public class C
                {
                    public int Compute(ref int a, int b) => a + b;
                    public static implicit operator int(C value) => 0;
                    public void Dispose() { }
                }
            }
            """;

        var compilation = await TestSolutions.Create(("C.cs", source)).Projects.Single().GetCompilationAsync(TestContext.Current.CancellationToken);
        var type = compilation.GetTypeByMetadataName("N.C");
        var exchange = compilation.GetTypeByMetadataName("System.Threading.Interlocked").GetMembers("Exchange").OfType<IMethodSymbol>()
            .Single(m => m.Parameters.Length == 2 && m.Parameters[1].Type.SpecialType == SpecialType.System_Int32);

        string Id(ISymbol symbol) => IlspyLauncher.NavigationId(DocumentationCommentId.CreateDeclarationId(symbol));

        Assert.Equal("M:N.C.Compute(System.Int32@,System.Int32)", Id(type.GetMembers("Compute").Single()));
        Assert.Equal("M:N.C.Dispose", Id(type.GetMembers("Dispose").Single()));
        Assert.Equal("M:System.Threading.Interlocked.Exchange(System.Int32@,System.Int32)", Id(exchange));
        Assert.EndsWith("~System.Int32", Id(type.GetMembers("op_Implicit").Single()));
    }
}
