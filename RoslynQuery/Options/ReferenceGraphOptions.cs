using System.ComponentModel;

using Microsoft.VisualStudio.Shell;

namespace RoslynQuery.Options;

/// <summary>Tools &gt; Options &gt; RoslynQuery &gt; Reference Graph.</summary>
public sealed class ReferenceGraphOptions : DialogPage
{
    [Category("Metadata symbols")]
    [DisplayName("Open metadata symbols in")]
    [Description("What opens when you activate a row that came from a referenced assembly. ILSpy shows the symbol in its own assembly tree, where the whole assembly is browsable. Visual Studio decompiles the containing type into a temporary file and opens that in the editor. When no ILSpy can be found, Visual Studio is used and the status line says so.")]
    [DefaultValue(MetadataNavigationMode.Ilspy)]
    [TypeConverter(typeof(MetadataNavigationModeConverter))]
    public MetadataNavigationMode MetadataNavigation { get; set; } = MetadataNavigationMode.Ilspy;

    [Category("Metadata symbols")]
    [DisplayName("ILSpy path")]
    [Description("Full path to ILSpy.exe. Leave this empty to search the usual install locations and the ILSpy extension for Visual Studio. Set it only when ILSpy lives somewhere unusual; a path that does not exist is reported rather than ignored.")]
    public string IlspyPath { get; set; }

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
}
