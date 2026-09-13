using System;
using System.IO;

namespace RoslynQuery.Navigation;

/// <summary>How far <see cref="ImplementationAssemblyResolver"/> may look past an exact version match.</summary>
internal readonly struct ResolveOptions
{
    public ResolveOptions(RollForwardPolicy policy, bool rollToPrerelease, bool allowNuGetFallback, string nuGetRoot, string policyDescription = null)
    {
        Policy = policy;
        RollToPrerelease = rollToPrerelease;
        AllowNuGetFallback = allowNuGetFallback;
        NuGetRoot = nuGetRoot;
        PolicyDescription = policyDescription;
    }

    public RollForwardPolicy Policy { get; }
    public bool RollToPrerelease { get; }
    public bool AllowNuGetFallback { get; }
    public string NuGetRoot { get; }

    /// <summary>The policy as a user should read it, including where it came from.</summary>
    public string PolicyDescription { get; }

    public const string NuGetPackagesVariable = "NUGET_PACKAGES";

    /// <summary>The process's own roll-forward settings, which for Visual Studio are whatever it was started with.</summary>
    public static ResolveOptions FromEnvironment(bool allowNuGetFallback)
    {
        var rawPolicy = Environment.GetEnvironmentVariable(RuntimeRollForward.PolicyVariable);
        var rawPrerelease = Environment.GetEnvironmentVariable(RuntimeRollForward.PrereleaseVariable);

        RuntimeRollForward.TryParse(rawPolicy, out var policy);
        var rollToPrerelease = RuntimeRollForward.RollsToPrerelease(rawPrerelease);

        return new ResolveOptions(
            policy, rollToPrerelease, allowNuGetFallback,
            NuGetRootFrom(Environment.GetEnvironmentVariable(NuGetPackagesVariable)),
            Describe(rawPolicy, rawPrerelease));
    }

    /// <summary>The policy the raw variables produce, worded so a user can see why it is the one in force.</summary>
    public static string Describe(string rawPolicy, string rawPrerelease) =>
        RuntimeRollForward.RollsToPrerelease(rawPrerelease)
            ? DescribePolicy(rawPolicy) + ", with previews allowed by " + RuntimeRollForward.PrereleaseVariable
            : DescribePolicy(rawPolicy);

    public static string DescribePolicy(string rawPolicy)
    {
        var parsed = RuntimeRollForward.TryParse(rawPolicy, out var policy);

        return string.IsNullOrEmpty(rawPolicy) ? policy + " (" + RuntimeRollForward.PolicyVariable + " is not set)"
            : parsed ? policy + " (from " + RuntimeRollForward.PolicyVariable + ")"
            : policy + " (" + RuntimeRollForward.PolicyVariable + "='" + rawPolicy + "' is not a policy)";
    }

    public static string DescribePrerelease(string rawPrerelease) =>
        string.IsNullOrEmpty(rawPrerelease) ? "No (" + RuntimeRollForward.PrereleaseVariable + " is not set)"
        : RuntimeRollForward.RollsToPrerelease(rawPrerelease) ? "Yes (" + RuntimeRollForward.PrereleaseVariable + "='" + rawPrerelease + "')"
        : "No (" + RuntimeRollForward.PrereleaseVariable + "='" + rawPrerelease + "' does not read as 1)";

    public static string DescribeNuGetRoot(string rawNuGetPackages)
    {
        var root = NuGetRootFrom(rawNuGetPackages);
        var source = string.IsNullOrWhiteSpace(rawNuGetPackages) ? NuGetPackagesVariable + " is not set" : "from " + NuGetPackagesVariable;

        return root + " (" + source + (Directory.Exists(root) ? ")" : ", and it does not exist)");
    }

    public static string NuGetRootFrom(string rawNuGetPackages) =>
        !string.IsNullOrWhiteSpace(rawNuGetPackages)
            ? rawNuGetPackages.Trim()
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
}
