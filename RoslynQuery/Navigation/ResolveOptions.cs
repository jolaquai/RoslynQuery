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
        var raw = Environment.GetEnvironmentVariable(RuntimeRollForward.PolicyVariable);
        var parsed = RuntimeRollForward.TryParse(raw, out var policy);

        var description = string.IsNullOrWhiteSpace(raw) ? policy + " (" + RuntimeRollForward.PolicyVariable + " is not set)"
            : parsed ? policy + " (from " + RuntimeRollForward.PolicyVariable + ")"
            : policy + " (" + RuntimeRollForward.PolicyVariable + "=" + raw.Trim() + " is not a policy)";

        return new ResolveOptions(policy, RuntimeRollForward.CurrentRollsToPrerelease, allowNuGetFallback, DefaultNuGetRoot(), description);
    }

    private static string DefaultNuGetRoot()
    {
        var configured = Environment.GetEnvironmentVariable("NUGET_PACKAGES");

        return !string.IsNullOrWhiteSpace(configured)
            ? configured.Trim()
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
    }
}
