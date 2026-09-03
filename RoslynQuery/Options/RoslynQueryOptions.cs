using System.ComponentModel;

using Microsoft.VisualStudio.Shell;

using RoslynQuery.Query;

namespace RoslynQuery.Options;

/// <summary>Tools &gt; Options &gt; RoslynQuery &gt; General. Persisted by the base class via VS's settings store.</summary>
public sealed class RoslynQueryOptions : DialogPage
{
    [Category("Roslyn Query")]
    [DisplayName("Default scope")]
    [Description("The scope the Search/Replace tool window's Scope box starts on each time it loads.")]
    [DefaultValue(ScopeKind.Document)]
    public ScopeKind DefaultScope { get; set; } = ScopeKind.Document;
}
