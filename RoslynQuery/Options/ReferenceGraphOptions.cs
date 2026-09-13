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

    [Category("Metadata symbols")]
    [DisplayName("Show metadata consumers of metadata symbols")]
    [Description("Whether a branch listing what depends on a symbol from a referenced assembly also lists dependents that come from referenced assemblies. Off, Implemented By on IDisposable shows only the types in your solution that implement it, not the hundreds of framework types that do. Branches listing what a symbol builds on, such as Overrides and Implements, always show everything.")]
    [DefaultValue(false)]
    public bool ShowMetadataConsumers { get; set; }

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
