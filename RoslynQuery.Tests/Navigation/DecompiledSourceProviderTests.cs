using System;
using System.IO;
using System.Linq;

using RoslynQuery.Navigation;

using Xunit;

namespace RoslynQuery.Tests;

// Decompiles assemblies installed on the machine; a test skips where its layout is absent.
public class DecompiledSourceProviderTests
{
    private const string ReadBytes = "M:System.IO.MemoryStream.Read(System.Byte[],System.Int32,System.Int32)";

    private static string[] Lines(DecompiledSource source) => source.Text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

    private static string LineAt(DecompiledSource source) => Lines(source)[source.Line].Trim();

    private static DecompiledSource FromImplementation(string documentationId)
    {
        var source = DecompiledSourceProvider.Decompile(InstalledAssemblies.NetFrameworkImplementation, documentationId);
        Assert.True(source.Succeeded, source.Failure);

        return source;
    }

    [Fact]
    public void FromTheNetFrameworkReferenceAssembly_ShowsTheRealBodyAtTheMember()
    {
        var reference = InstalledAssemblies.NetFrameworkReference("mscorlib.dll");
        Assert.SkipUnless(File.Exists(reference), ".NET Framework 4.7.2 reference assemblies are not installed.");

        var source = DecompiledSourceProvider.Decompile(reference, ReadBytes);

        Assert.True(source.Succeeded, source.Failure);
        Assert.Contains("_position", source.Text);
        Assert.DoesNotContain("Empty body found", source.Text);
        Assert.StartsWith("public override int Read(", LineAt(source));
    }

    [Fact]
    public void FromANetReferencePack_FollowsTheForwardIntoTheDeclaringAssembly()
    {
        var reference = InstalledAssemblies.NetReferencePack("System.Runtime.dll");
        Assert.SkipUnless(reference != null, "No .NET reference pack is installed.");

        var source = DecompiledSourceProvider.Decompile(reference, ReadBytes);

        Assert.True(source.Succeeded, source.Failure);
        Assert.Equal("System.Private.CoreLib", source.AssemblyName);
        Assert.Contains("_position", source.Text);
        Assert.StartsWith("public override int Read(byte[] buffer", LineAt(source));
    }

    [Fact]
    public void Overloads_EachLandOnTheirOwnDeclaration()
    {
        var single = FromImplementation("M:System.String.Format(System.String,System.Object)");
        var array = FromImplementation("M:System.String.Format(System.String,System.Object[])");

        Assert.NotEqual(single.Line, array.Line);
        Assert.Contains("object arg0", LineAt(single));
        Assert.Contains("params object[] args", LineAt(array));
    }

    [Fact]
    public void AnIndexer_LandsOnItsDeclaration() =>
        Assert.Contains("this[int index]", LineAt(FromImplementation("P:System.Collections.Generic.List`1.Item(System.Int32)")));

    [Fact]
    public void AnOperator_LandsOnItsDeclaration() =>
        Assert.Contains("operator ==", LineAt(FromImplementation("M:System.String.op_Equality(System.String,System.String)")));

    /// <summary>List&lt;T&gt; nests a synchronized wrapper whose Add has the identical signature, so matching text alone could pick either.</summary>
    [Fact]
    public void AMemberRepeatedInsideANestedType_LandsOnTheOuterTypesOwnDeclaration()
    {
        var source = FromImplementation("M:System.Collections.Generic.List`1.Add(`0)");
        var lines = Lines(source);

        static int Depth(string line) => line.TakeWhile(c => c == '\t').Count();

        var candidates = Enumerable.Range(0, lines.Length).Where(i => lines[i].Trim() == "public void Add(T item)").ToList();

        Assert.True(candidates.Count > 1, "The nested declaration with the same signature is gone, so this no longer exercises the ambiguity.");
        Assert.Contains(source.Line, candidates);
        Assert.Equal(candidates.Min(i => Depth(lines[i])), Depth(lines[source.Line]));
    }

    [Fact]
    public void ATypeRow_LandsOnTheTypeDeclaration() =>
        Assert.Contains("class MemoryStream", LineAt(FromImplementation("T:System.IO.MemoryStream")));

    [Fact]
    public void AMissingMember_FailsWithAReasonInsteadOfThrowing()
    {
        var source = DecompiledSourceProvider.Decompile(InstalledAssemblies.NetFrameworkImplementation, "M:System.IO.MemoryStream.NoSuchMember");

        Assert.False(source.Succeeded);
        Assert.Contains("NoSuchMember", source.Failure);
    }

    [Fact]
    public void AReferenceAssemblyWithNothingBehindIt_IsRefusedRatherThanShownAsStubs()
    {
        var reference = InstalledAssemblies.NetReferencePack("System.Runtime.dll");
        Assert.SkipUnless(reference != null, "No .NET reference pack is installed.");

        var directory = Path.Combine(Path.GetTempPath(), "RoslynQueryTests", Guid.NewGuid().ToString("N"));
        var copy = Path.Combine(directory, "System.Runtime.dll");
        Directory.CreateDirectory(directory);
        File.Copy(reference, copy);

        try
        {
            var source = DecompiledSourceProvider.Decompile(copy, ReadBytes);

            Assert.False(source.Succeeded);
            Assert.Contains("reference assembly", source.Failure);
            Assert.Null(source.Text);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TheDecompilerForAnAssembly_IsBuiltOnlyOnce()
    {
        FromImplementation("T:System.IO.MemoryStream");
        var built = DecompiledSourceProvider.DecompilersCreated;

        FromImplementation("T:System.IO.Stream");

        Assert.Equal(built, DecompiledSourceProvider.DecompilersCreated);
    }
}
