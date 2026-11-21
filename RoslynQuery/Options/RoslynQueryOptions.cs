using System.ComponentModel;

using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Settings;

using RoslynQuery.Query;

namespace RoslynQuery.Options;

/// <summary>
/// Tools &gt; Options &gt; RoslynQuery &gt; General. DialogPage persists every property below to VS's
/// settings store on its own - they survive closing and reopening Visual Studio without any extra
/// code here, and take effect the next time a tool window loads.
/// </summary>
public sealed class RoslynQueryOptions : DialogPage
{
    [Category("Tool Window Defaults")]
    [DisplayName("Default target")]
    [Description("The Target the Search/Replace tool window's Find box starts on each time it loads.")]
    [DefaultValue(TargetKind.SyntaxNode)]
    public TargetKind DefaultTarget { get; set; } = TargetKind.SyntaxNode;

    [Category("Tool Window Defaults")]
    [DisplayName("Default scope")]
    [Description("The scope the Search/Replace tool window's Scope box starts on each time it loads.")]
    [DefaultValue(ScopeKind.Document)]
    public ScopeKind DefaultScope { get; set; } = ScopeKind.Document;

    [Category("Tool Window Defaults")]
    [DisplayName("Default cap")]
    [Description("The match cap the tool window starts with.")]
    [DefaultValue(CapPreset.Cap5000)]
    public CapPreset DefaultCap { get; set; } = CapPreset.Cap5000;

    [Category("Tool Window Defaults")]
    [DisplayName("Include generated code by default")]
    [Description("Whether the Generated checkbox starts checked.")]
    [DefaultValue(false)]
    public bool DefaultIncludeGenerated { get; set; }

    [Category("Tool Window Defaults")]
    [DisplayName("Show history by default")]
    [Description("Whether the query history sidebar starts expanded.")]
    [DefaultValue(true)]
    public bool DefaultShowHistory { get; set; } = true;

    [Category("Tool Window Defaults")]
    [DisplayName("Show \"Need help?\" link")]
    [Description("Whether the tool window shows a link to the documentation beside the signature line.")]
    [DefaultValue(true)]
    public bool ShowNeedHelpLink { get; set; } = true;

    [Category("Tool Window Defaults")]
    [DisplayName("Show example query")]
    [Description("Whether the Find box starts pre-filled with the example query. Turn off to start with an empty box.")]
    [DefaultValue(true)]
    public bool ShowExampleQuery { get; set; } = true;

    [Category("Agent Bridge")]
    [DisplayName("Start the agent bridge with Visual Studio")]
    [Description("Lets the roslynquery CLI run against this Visual Studio's live workspace (unsaved edits included). The bridge executes C# that the CLI sends, over a token-guarded pipe only this Windows user can open. The Roslyn Query menu's Enable/Disable Agent Bridge command flips it too.")]
    [DefaultValue(true)]
    public bool AutoStartAgentBridge { get; set; } = true;

    public const string SettingsCollection = "RoslynQuery";
    public const string BridgeDisabledProperty = "AgentBridgeDisabled";

    public override void SaveSettingsToStorage()
    {
        base.SaveSettingsToStorage();
        SyncBridgeDisabledFlag();
    }

    /// <summary>DialogPage stores bools as strings, which a UIContext rule's UserSettingsStoreQuery cannot read; this int mirror is what the auto-load rule tests.</summary>
    public void SyncBridgeDisabledFlag()
    {
        var store = new ShellSettingsManager(ServiceProvider.GlobalProvider).GetWritableSettingsStore(SettingsScope.UserSettings);
        if (!store.CollectionExists(SettingsCollection)) store.CreateCollection(SettingsCollection);
        store.SetInt32(SettingsCollection, BridgeDisabledProperty, AutoStartAgentBridge ? 0 : 1);
    }
}
