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

    /// <summary>The process's own roll-forward settings, which for Visual Studio are whatever it was started with.</summary>
    public static ResolveOptions FromEnvironment(bool allowNuGetFallback)
    {
        var rawPolicy = Environment.GetEnvironmentVariable(RuntimeRollForward.PolicyVariable);
        var rawPrerelease = Environment.GetEnvironmentVariable(RuntimeRollForward.PrereleaseVariable);

        RuntimeRollForward.TryParse(rawPolicy, out var policy);
        var rollToPrerelease = RuntimeRollForward.RollsToPrerelease(rawPrerelease);

        return new ResolveOptions(policy, rollToPrerelease, allowNuGetFallback, DefaultNuGetRoot(), Describe(rawPolicy, rawPrerelease));
    }

    /// <summary>The policy the raw variables produce, worded so a user can see why it is the one in force.</summary>
    public static string Describe(string rawPolicy, string rawPrerelease)
    {
        var parsed = RuntimeRollForward.TryParse(rawPolicy, out var policy);

        var description = string.IsNullOrEmpty(rawPolicy) ? policy + " (" + RuntimeRollForward.PolicyVariable + " is not set)"
            : parsed ? policy + " (from " + RuntimeRollForward.PolicyVariable + ")"
            : policy + " (" + RuntimeRollForward.PolicyVariable + "='" + rawPolicy + "' is not a policy)";

        return RuntimeRollForward.RollsToPrerelease(rawPrerelease)
            ? description + ", with previews allowed by " + RuntimeRollForward.PrereleaseVariable
            : description;
    }

    private static string DefaultNuGetRoot()
    {
        var configured = Environment.GetEnvironmentVariable("NUGET_PACKAGES");

        return !string.IsNullOrWhiteSpace(configured)
            ? configured.Trim()
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
    }
}
