using System.ComponentModel;

using Microsoft.VisualStudio.Shell;

namespace RoslynQuery.Options;

/// <summary>Tools &gt; Options &gt; RoslynQuery &gt; Reference Graph.</summary>
public sealed class ReferenceGraphOptions : DialogPage
{
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
