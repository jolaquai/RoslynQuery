using System;
using System.ComponentModel;

using Microsoft.VisualStudio.Shell;

using RoslynQuery.Navigation;

namespace RoslynQuery.Options;

/// <summary>Tools &gt; Options &gt; RoslynQuery &gt; Environment. Nothing to set: what Visual Studio's own environment resolves to.</summary>
public sealed class EnvironmentOptions : DialogPage
{
    // Hidden from serialization, so DialogPage never saves these and never assigns them on load.
    [Category("Environment variables (read-only)")]
    [DisplayName("Roll-forward policy")]
    [Description("The roll-forward policy this Visual Studio process has, from DOTNET_ROLL_FORWARD. It decides which installed runtime a metadata row opens from when the reference pack's exact version is not installed. Visual Studio reads its environment once at startup, so set the variable before starting it.")]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string RollForward =>
        ResolveOptions.DescribePolicy(Environment.GetEnvironmentVariable(RuntimeRollForward.PolicyVariable));

    [Category("Environment variables (read-only)")]
    [DisplayName("Roll forward to previews")]
    [Description("Whether DOTNET_ROLL_FORWARD_TO_PRERELEASE lets roll-forward pick a preview runtime even when a release qualifies. Without it a preview is only picked when no release does. It is on only when the value reads as the number 1, exactly as .NET itself reads it.")]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string RollForwardToPreviews =>
        ResolveOptions.DescribePrerelease(Environment.GetEnvironmentVariable(RuntimeRollForward.PrereleaseVariable));

    [Category("Environment variables (read-only)")]
    [DisplayName("NuGet package cache")]
    [Description("Where Fall back to NuGet packages, on the Reference Graph page, looks: NUGET_PACKAGES, or the default cache under your profile when that is not set.")]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string NuGetPackageCache =>
        ResolveOptions.DescribeNuGetRoot(Environment.GetEnvironmentVariable(ResolveOptions.NuGetPackagesVariable));

    [Category("Environment variables (read-only)")]
    [DisplayName(".NET install root")]
    [Description("Where installed runtimes are looked up for a reference pack that was restored into the NuGet cache: DOTNET_ROOT, or Program Files when that is not set or does not exist. A reference pack under an install's own packs folder always uses that install instead.")]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string DotNetInstallRoot =>
        ImplementationAssemblyResolver.DescribeDotNetRoot(Environment.GetEnvironmentVariable(ImplementationAssemblyResolver.DotNetRootVariable));
}
