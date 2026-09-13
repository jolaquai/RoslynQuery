using System;
using System.ComponentModel;

using Microsoft.VisualStudio.Shell;

using RoslynQuery.Navigation;
using RoslynQuery.Query;

namespace RoslynQuery.Options;

/// <summary>Tools &gt; Options &gt; RoslynQuery &gt; Reference Graph.</summary>
public sealed class ReferenceGraphOptions : DialogPage
{
    [Category("Tool Window Defaults")]
    [DisplayName("Default scope")]
    [Description("The scope the Reference Graph window's Scope box starts on each time it loads.")]
    [DefaultValue(ScopeKind.Project)]
    [TypeConverter(typeof(ReferenceGraphScopeConverter))]
    public ScopeKind DefaultScope { get; set; } = ScopeKind.Project;

    [Category("Metadata symbols")]
    [DisplayName("Open metadata symbols in")]
    [Description("What opens when you activate a row that came from a referenced assembly. ILSpy shows the symbol in its own assembly tree, where the whole assembly is browsable. Visual Studio decompiles the containing type into a temporary file and opens that in the editor. When ILSpy is chosen but cannot be found, a message box says so.")]
    [DefaultValue(MetadataNavigationMode.Ilspy)]
    [TypeConverter(typeof(MetadataNavigationModeConverter))]
    public MetadataNavigationMode MetadataNavigation { get; set; } = MetadataNavigationMode.Ilspy;

    [Category("Metadata symbols")]
    [DisplayName("ILSpy path")]
    [Description("Full path to ILSpy.exe. Leave this empty to search the usual install locations and the ILSpy extension for Visual Studio. Set it only when ILSpy lives somewhere unusual; a path that does not exist is reported rather than ignored.")]
    public string IlspyPath { get; set; }

    [Category("Metadata symbols")]
    [DisplayName("Fall back to NuGet packages")]
    [Description("Framework assemblies are opened from the installed .NET runtime matching the project's reference pack, or, failing an exact match, from whichever runtime DOTNET_ROLL_FORWARD allows. When that finds nothing, this also looks for the same assembly shipped as a NuGet package in the local package cache, by the same rule: the exact version first, then DOTNET_ROLL_FORWARD. Off by default, since a package build is not always the build a runtime ships.")]
    [DefaultValue(false)]
    public bool FallBackToNuGetPackages { get; set; }

    // Hidden from serialization, so DialogPage never saves these and never assigns them on load.
    [Category("Environment (read-only)")]
    [DisplayName("Roll-forward policy")]
    [Description("The roll-forward policy this Visual Studio process has, from DOTNET_ROLL_FORWARD. It decides which installed runtime a metadata row opens from when the reference pack's exact version is not installed. Visual Studio reads its environment once at startup, so set the variable before starting it.")]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string EnvironmentRollForward =>
        ResolveOptions.DescribePolicy(Environment.GetEnvironmentVariable(RuntimeRollForward.PolicyVariable));

    [Category("Environment (read-only)")]
    [DisplayName("Roll forward to previews")]
    [Description("Whether DOTNET_ROLL_FORWARD_TO_PRERELEASE lets roll-forward pick a preview runtime even when a release qualifies. Without it a preview is only picked when no release does. It is on only when the value reads as the number 1, exactly as .NET itself reads it.")]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string EnvironmentRollForwardToPreviews =>
        ResolveOptions.DescribePrerelease(Environment.GetEnvironmentVariable(RuntimeRollForward.PrereleaseVariable));

    [Category("Environment (read-only)")]
    [DisplayName("NuGet package cache")]
    [Description("Where Fall back to NuGet packages looks, from NUGET_PACKAGES, or the default cache under your profile when that is not set.")]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string EnvironmentNuGetPackageCache =>
        ResolveOptions.DescribeNuGetRoot(Environment.GetEnvironmentVariable(ResolveOptions.NuGetPackagesVariable));

    [Category("Environment (read-only)")]
    [DisplayName(".NET install root")]
    [Description("Where installed runtimes are looked up for a reference pack that was restored into the NuGet cache, from DOTNET_ROOT, or Program Files when that is not set or does not exist. A reference pack under an install's own packs folder always uses that install instead.")]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string EnvironmentDotNetRoot =>
        ImplementationAssemblyResolver.DescribeDotNetRoot(Environment.GetEnvironmentVariable(ImplementationAssemblyResolver.DotNetRootVariable));

    // Inert until IL analysis exists: a persisted true must never switch anything on.
    [Category("IL analysis (not yet available)")]
    [DisplayName("Enable IL analysis")]
    [Description("NOT YET AVAILABLE - this setting does nothing yet. Once implemented, it lets Uses continue past your source into referenced assemblies by reading the called method's IL.")]
    [DefaultValue(false)]
    [ReadOnly(true)]
    public bool EnableIlAnalysis
    {
        get => false;
        set { }
    }

    [Category("IL analysis (not yet available)")]
    [DisplayName("Enable reverse IL analysis")]
    [Description("NOT YET AVAILABLE - this setting does nothing yet. WARNING: reverse IL analysis would make Used By report framework code that calls into YOUR code. That almost never happens, so it rarely finds anything worth seeing, and finding out means scanning every method body in every referenced assembly. Leave it off unless you know you need it.")]
    [DefaultValue(false)]
    [ReadOnly(true)]
    public bool EnableReverseIlAnalysis
    {
        get => false;
        set { }
    }

    /// <summary>Clearing or changing the path has to re-arm the one-shot autodetect.</summary>
    protected override void OnApply(PageApplyEventArgs e)
    {
        base.OnApply(e);
        IlspyLocator.Reset();
    }
}
