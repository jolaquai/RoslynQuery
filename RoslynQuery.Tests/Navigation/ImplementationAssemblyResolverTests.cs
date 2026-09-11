using System;
using System.IO;
using System.Linq;

using RoslynQuery.Navigation;

using Xunit;

namespace RoslynQuery.Tests;

// Runs against the reference and implementation assemblies installed on the machine; a test skips where its layout is absent.
public class ImplementationAssemblyResolverTests
{
    private static readonly string FrameworkReferences = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        @"Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2");

    private static string NetReferencePack(string file)
    {
        var packs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"dotnet\packs\Microsoft.NETCore.App.Ref");
        if (!Directory.Exists(packs)) return null;

        return Directory.EnumerateDirectories(packs)
            .Where(v => Directory.Exists(Path.Combine(v, "ref")))
            .SelectMany(v => Directory.EnumerateDirectories(Path.Combine(v, "ref")))
            .Select(tfm => Path.Combine(tfm, file))
            .Where(File.Exists)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static (string Reference, string Lib) NuGetReferenceWithLib()
    {
        var root = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrEmpty(root)) root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        if (!Directory.Exists(root)) return default;

        foreach (var version in Directory.EnumerateDirectories(root).SelectMany(Directory.EnumerateDirectories))
        {
            var refRoot = Path.Combine(version, "ref");
            if (!Directory.Exists(refRoot)) continue;

            foreach (var tfm in Directory.EnumerateDirectories(refRoot))
            foreach (var dll in Directory.EnumerateFiles(tfm, "*.dll"))
            {
                var lib = Path.Combine(version, "lib", Path.GetFileName(tfm), Path.GetFileName(dll));
                if (File.Exists(lib) && ImplementationAssemblyResolver.IsReferenceAssembly(dll)) return (dll, lib);
            }
        }

        return default;
    }

    [Fact]
    public void AnImplementationAssembly_ResolvesToItself()
    {
        var implementation = typeof(object).Assembly.Location;

        Assert.False(ImplementationAssemblyResolver.IsReferenceAssembly(implementation));
        Assert.Equal(implementation, ImplementationAssemblyResolver.Resolve(implementation));
    }

    [Fact]
    public void AMissingFile_ResolvesToNull() =>
        Assert.Null(ImplementationAssemblyResolver.Resolve(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dll")));

    [Theory]
    [InlineData("mscorlib.dll")]
    [InlineData("System.Core.dll")]
    [InlineData(@"Facades\System.Runtime.dll")]
    [InlineData("WindowsBase.dll")]
    public void ANetFrameworkReferenceAssembly_ResolvesToAnImplementationOfTheSameName(string relative)
    {
        var reference = Path.Combine(FrameworkReferences, relative);
        Assert.SkipUnless(File.Exists(reference), ".NET Framework 4.7.2 reference assemblies are not installed.");
        Assert.True(ImplementationAssemblyResolver.IsReferenceAssembly(reference));

        var resolved = ImplementationAssemblyResolver.Resolve(reference);

        Assert.NotNull(resolved);
        Assert.Equal(Path.GetFileName(reference), Path.GetFileName(resolved), ignoreCase: true);
        Assert.False(ImplementationAssemblyResolver.IsReferenceAssembly(resolved));
    }

    [Fact]
    public void ANetReferencePackAssembly_ResolvesIntoTheSharedRuntime()
    {
        var reference = NetReferencePack("System.Runtime.dll");
        Assert.SkipUnless(reference != null, "No .NET reference pack is installed.");

        var resolved = ImplementationAssemblyResolver.Resolve(reference);

        Assert.NotNull(resolved);
        Assert.Contains(@"\shared\Microsoft.NETCore.App\", resolved, StringComparison.OrdinalIgnoreCase);
        Assert.False(ImplementationAssemblyResolver.IsReferenceAssembly(resolved));
    }

    /// <summary>The GAC holds a .NET Framework System.Runtime of the same name and key; showing it for a .NET type would be wrong code.</summary>
    [Fact]
    public void ANetReferenceAssemblyOutsideItsPack_IsNeverMatchedToTheNetFrameworkGac()
    {
        var reference = NetReferencePack("System.Runtime.dll");
        Assert.SkipUnless(reference != null, "No .NET reference pack is installed.");

        var directory = Path.Combine(Path.GetTempPath(), "RoslynQueryTests", Guid.NewGuid().ToString("N"));
        var copy = Path.Combine(directory, "System.Runtime.dll");
        Directory.CreateDirectory(directory);
        File.Copy(reference, copy);

        try
        {
            Assert.Null(ImplementationAssemblyResolver.Resolve(copy));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ANuGetReferenceAssembly_ResolvesToTheLibBesideIt()
    {
        var (reference, lib) = NuGetReferenceWithLib();
        Assert.SkipUnless(reference != null, "No NuGet package with both ref and lib assemblies is in the local cache.");

        Assert.Equal(lib, ImplementationAssemblyResolver.Resolve(reference), ignoreCase: true);
    }
}
