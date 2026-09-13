using System;
using System.IO;
using System.Linq;

using RoslynQuery.Navigation;

using Xunit;

namespace RoslynQuery.Tests;

/// <summary>
/// Reference-pack resolution against layouts assembled in a temp directory from copies of one real reference
/// assembly and its real implementation, so every choice is checked against files the resolver actually reads.
/// </summary>
public class ReferencePackResolutionTests
{
    private const string FileName = "System.Diagnostics.DiagnosticSource.dll";
    private const string PackageId = "system.diagnostics.diagnosticsource";

    private static readonly string DotNet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");

    private static (string Reference, string Implementation) RealPair()
    {
        var packs = Path.Combine(DotNet, @"packs\Microsoft.NETCore.App.Ref");
        var shared = Path.Combine(DotNet, @"shared\Microsoft.NETCore.App");
        if (!Directory.Exists(packs) || !Directory.Exists(shared)) return default;

        foreach (var version in Directory.EnumerateDirectories(packs))
        {
            var implementation = Path.Combine(shared, Path.GetFileName(version), FileName);
            var refRoot = Path.Combine(version, "ref");
            if (!File.Exists(implementation) || !Directory.Exists(refRoot)) continue;

            var reference = Directory.EnumerateFiles(refRoot, FileName, SearchOption.AllDirectories).FirstOrDefault();
            if (reference != null) return (reference, implementation);
        }

        return default;
    }

    private sealed class Layout : IDisposable
    {
        private readonly string _reference;
        private readonly string _implementation;

        public Layout()
        {
            (_reference, _implementation) = RealPair();
            Assert.SkipUnless(_reference != null, "No installed .NET reference pack with a matching shared runtime to copy from.");

            Root = Path.Combine(Path.GetTempPath(), "RoslynQueryTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string NuGet => Path.Combine(Root, "nuget");

        public string Pack(string version, string framework = "net9.0") =>
            Copy(_reference, Path.Combine(Root, "packs", "Microsoft.NETCore.App.Ref", version, "ref", framework));

        public string Runtime(string version) =>
            Copy(_implementation, Path.Combine(Root, "shared", "Microsoft.NETCore.App", version));

        public string Package(string version, string framework) =>
            Copy(_implementation, Path.Combine(NuGet, PackageId, version, "lib", framework));

        public ResolveOptions Options(RollForwardPolicy policy = RollForwardPolicy.Minor, bool nuGet = false, bool prerelease = false) =>
            new ResolveOptions(policy, prerelease, nuGet, NuGet);

        private static string Copy(string source, string directory)
        {
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, FileName);
            File.Copy(source, target, overwrite: true);
            return target;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception)
            {
            }
        }
    }

    [Fact]
    public void TheExactRuntime_WinsOverANewerPatch()
    {
        using var layout = new Layout();
        var reference = layout.Pack("9.0.17");
        var exact = layout.Runtime("9.0.17");
        layout.Runtime("9.0.20");

        Assert.Equal(exact, ImplementationAssemblyResolver.Resolve(reference, layout.Options()), ignoreCase: true);
    }

    [Fact]
    public void WithoutAnExactRuntime_MinorRollsToTheLatestPatchOfTheSameMinor()
    {
        using var layout = new Layout();
        var reference = layout.Pack("9.0.17");
        layout.Runtime("9.0.12");
        var latest = layout.Runtime("9.0.20");

        Assert.Equal(latest, ImplementationAssemblyResolver.Resolve(reference, layout.Options(RollForwardPolicy.Minor)), ignoreCase: true);
    }

    [Fact]
    public void WithoutTheMajor_MinorFindsNothingAndMajorTakesTheNextOne()
    {
        using var layout = new Layout();
        var reference = layout.Pack("9.0.17");
        layout.Runtime("8.0.31");
        layout.Runtime("10.0.11");
        var next = layout.Runtime("10.0.12");

        Assert.Null(ImplementationAssemblyResolver.Resolve(reference, layout.Options(RollForwardPolicy.Minor)));
        Assert.Equal(next, ImplementationAssemblyResolver.Resolve(reference, layout.Options(RollForwardPolicy.Major)), ignoreCase: true);
    }

    [Fact]
    public void Disable_TakesOnlyTheExactRuntime()
    {
        using var layout = new Layout();
        var reference = layout.Pack("9.0.17");
        layout.Runtime("9.0.20");

        Assert.Null(ImplementationAssemblyResolver.Resolve(reference, layout.Options(RollForwardPolicy.Disable)));
    }

    [Fact]
    public void TheNuGetFallback_IsNotUsedUnlessAllowed()
    {
        using var layout = new Layout();
        var reference = layout.Pack("9.0.17");
        layout.Package("9.0.17", "net9.0");

        Assert.Null(ImplementationAssemblyResolver.Resolve(reference, layout.Options(nuGet: false)));
    }

    [Fact]
    public void TheNuGetFallback_TakesTheExactPackageVersionFirst()
    {
        using var layout = new Layout();
        var reference = layout.Pack("9.0.17");
        layout.Package("9.0.0", "net9.0");
        var exact = layout.Package("9.0.17", "net9.0");

        Assert.Equal(exact, ImplementationAssemblyResolver.Resolve(reference, layout.Options(nuGet: true)), ignoreCase: true);
    }

    [Fact]
    public void TheNuGetFallback_RollsForwardByTheSamePolicy()
    {
        using var layout = new Layout();
        var reference = layout.Pack("9.0.17");
        var sameMinor = layout.Package("9.0.0", "net9.0");
        var nextMajor = layout.Package("10.0.0", "net9.0");

        Assert.Equal(sameMinor, ImplementationAssemblyResolver.Resolve(reference, layout.Options(RollForwardPolicy.Minor, nuGet: true)), ignoreCase: true);
        Assert.Equal(nextMajor, ImplementationAssemblyResolver.Resolve(reference, layout.Options(RollForwardPolicy.LatestMajor, nuGet: true)), ignoreCase: true);
    }

    [Fact]
    public void AnyRuntime_ComesBeforeAnyPackage()
    {
        using var layout = new Layout();
        var reference = layout.Pack("9.0.17");
        var rolled = layout.Runtime("10.0.12");
        layout.Package("9.0.17", "net9.0");

        Assert.Equal(rolled, ImplementationAssemblyResolver.Resolve(reference, layout.Options(RollForwardPolicy.Major, nuGet: true)), ignoreCase: true);
    }

    [Fact]
    public void APackage_PrefersTheReferencesOwnFramework()
    {
        using var layout = new Layout();
        var reference = layout.Pack("9.0.17");
        layout.Package("9.0.17", "net462");
        layout.Package("9.0.17", "net8.0");
        layout.Package("9.0.17", "netstandard2.0");
        var own = layout.Package("9.0.17", "net9.0");

        Assert.Equal(own, ImplementationAssemblyResolver.Resolve(reference, layout.Options(nuGet: true)), ignoreCase: true);
    }

    /// <summary>A .NET Framework build of the package is a different implementation, so it is never the answer for a .NET reference.</summary>
    [Fact]
    public void APackage_FallsBackToALowerDotNetBuild_ThenDotNetStandard_NeverDotNetFramework()
    {
        using var layout = new Layout();
        var reference = layout.Pack("9.0.17");

        layout.Package("9.0.17", "net462");
        Assert.Null(ImplementationAssemblyResolver.Resolve(reference, layout.Options(nuGet: true)));

        var standard = layout.Package("9.0.17", "netstandard2.0");
        Assert.Equal(standard, ImplementationAssemblyResolver.Resolve(reference, layout.Options(nuGet: true)), ignoreCase: true);

        var lower = layout.Package("9.0.17", "net8.0");
        Assert.Equal(lower, ImplementationAssemblyResolver.Resolve(reference, layout.Options(nuGet: true)), ignoreCase: true);

        layout.Package("9.0.17", "net10.0");
        Assert.Equal(lower, ImplementationAssemblyResolver.Resolve(reference, layout.Options(nuGet: true)), ignoreCase: true);
    }

    [Fact]
    public void TheExplanation_NamesThePackThePolicyAndWhatIsInstalled()
    {
        using var layout = new Layout();
        var reference = layout.Pack("9.0.17");
        layout.Runtime("8.0.31");
        layout.Runtime("10.0.12");

        var off = ImplementationAssemblyResolver.ExplainUnresolved(reference, new ResolveOptions(RollForwardPolicy.Minor, false, false, layout.NuGet, "Minor (DOTNET_ROLL_FORWARD is not set)"));
        var on = ImplementationAssemblyResolver.ExplainUnresolved(reference, layout.Options(nuGet: true));

        Assert.Contains("9.0.17 reference pack", off);
        Assert.Contains("Minor (DOTNET_ROLL_FORWARD is not set)", off);
        Assert.Contains("10.0.12, 8.0.31", off);
        Assert.Contains("DOTNET_ROLL_FORWARD", off);
        Assert.Contains("NuGet packages is off", off);
        Assert.Contains("No NuGet package matched", on);
    }

    [Fact]
    public void TheExplanation_IsNullForAnAssemblyOutsideAReferencePack() =>
        Assert.Null(ImplementationAssemblyResolver.ExplainUnresolved(typeof(object).Assembly.Location, default));

    [Theory]
    [InlineData(null, null, "Minor (DOTNET_ROLL_FORWARD is not set)")]
    [InlineData("Major", null, "Major (from DOTNET_ROLL_FORWARD)")]
    [InlineData("latestmajor", null, "LatestMajor (from DOTNET_ROLL_FORWARD)")]
    [InlineData("Sideways", null, "Minor (DOTNET_ROLL_FORWARD='Sideways' is not a policy)")]
    [InlineData(" Major", null, "Minor (DOTNET_ROLL_FORWARD=' Major' is not a policy)")]
    [InlineData("LatestMajor", "1", "LatestMajor (from DOTNET_ROLL_FORWARD), with previews allowed by DOTNET_ROLL_FORWARD_TO_PRERELEASE")]
    [InlineData(null, "true", "Minor (DOTNET_ROLL_FORWARD is not set)")]
    public void TheDescription_SaysWhichPolicyIsInForceAndWhy(string rawPolicy, string rawPrerelease, string expected) =>
        Assert.Equal(expected, ResolveOptions.Describe(rawPolicy, rawPrerelease));

    [Fact]
    public void TheOptions_DescribeWhereThePolicyCameFrom()
    {
        var options = ResolveOptions.FromEnvironment(allowNuGetFallback: true);

        Assert.True(options.AllowNuGetFallback);
        Assert.Contains(options.Policy.ToString(), options.PolicyDescription);
        Assert.Contains("DOTNET_ROLL_FORWARD", options.PolicyDescription);
        Assert.False(string.IsNullOrEmpty(options.NuGetRoot));
    }

    /// <summary>
    /// The case that was reported: a net9.0 reference restored into the NuGet cache, on a machine with no .NET 9
    /// runtime, which opens from the DiagnosticSource package only once the fallback is allowed.
    /// </summary>
    [Fact]
    public void TheReportedNet9Case_OpensFromTheNuGetPackageOnceAllowed()
    {
        var nuget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        var refPacks = Path.Combine(nuget, "microsoft.netcore.app.ref");
        var shared = Path.Combine(DotNet, @"shared\Microsoft.NETCore.App");

        var reference = Directory.Exists(refPacks)
            ? Directory.EnumerateDirectories(refPacks, "9.0.*").Select(v => Path.Combine(v, @"ref\net9.0", FileName)).FirstOrDefault(File.Exists)
            : null;
        var package = Path.Combine(nuget, PackageId, "9.0.0", @"lib\net9.0", FileName);
        var hasNet9Runtime = Directory.Exists(shared) && Directory.EnumerateDirectories(shared, "9.*").Any();

        Assert.SkipUnless(reference != null && File.Exists(package) && !hasNet9Runtime,
            "Needs a restored net9.0 reference pack, the DiagnosticSource 9.0.0 package, and no .NET 9 runtime installed.");

        Assert.Null(ImplementationAssemblyResolver.Resolve(reference, new ResolveOptions(RollForwardPolicy.Minor, false, false, nuget)));
        Assert.Equal(package, ImplementationAssemblyResolver.Resolve(reference, new ResolveOptions(RollForwardPolicy.Minor, false, true, nuget)), ignoreCase: true);
    }
}
