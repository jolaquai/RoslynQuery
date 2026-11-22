using System;
using System.ComponentModel.Design;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

using RoslynQuery.Bridge;
using RoslynQuery.Options;
using RoslynQuery.Query;
using RoslynQuery.ReferenceGraph;
using RoslynQuery.ToolWindow;

using Task = System.Threading.Tasks.Task;

namespace RoslynQuery;

[Guid(PackageGuidString)]
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[InstalledProductRegistration("#110", "#112", "0.5.0")]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideToolWindow(typeof(QueryToolWindow), Style = VsDockStyle.Tabbed, Window = "{D78612C7-9962-4B83-95D9-268046DAD23A}")]
[ProvideToolWindow(typeof(ReferenceGraphToolWindow), Style = VsDockStyle.Tabbed, Window = "{D78612C7-9962-4B83-95D9-268046DAD23A}")]
[ProvideOptionPage(typeof(RoslynQueryOptions), "RoslynQuery", "General", 0, 0, true)]
[ProvideOptionPage(typeof(ReferenceGraphOptions), "RoslynQuery", "Reference Graph", 0, 0, true)]
[ProvideOptionPage(typeof(EnvironmentOptions), "RoslynQuery", "Environment", 0, 0, true)]
// Without this, "View Reference Graph" stays greyed out until a window is opened by hand: nothing
// else loads the package, so its BeforeQueryStatus never runs.
[ProvideAutoLoad(CSharpEditorContextGuidString, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideUIContextRule(
    CSharpEditorContextGuidString,
    name: "C# editor active",
    expression: "CSharp",
    termNames: ["CSharp"],
    termValues: ["ActiveEditorContentType:CSharp"])]
// Loads at every start so the stable CLI copy is refreshed after an update even before a solution is opened.
[ProvideAutoLoad(VSConstants.UICONTEXT.ShellInitialized_string, PackageAutoLoadFlags.BackgroundLoad)]
// The C# editor rule alone never fires when a solution opens with no C# document focused, so the bridge would not auto-start.
[ProvideAutoLoad(BridgeAutoStartContextGuidString, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideUIContextRule(
    BridgeAutoStartContextGuidString,
    name: "Solution loaded, agent bridge not disabled",
    expression: "Solution & !Disabled",
    termNames: ["Solution", "Disabled"],
    termValues: [VSConstants.UICONTEXT.SolutionExistsAndFullyLoaded_string, "UserSettingsStoreQuery:" + RoslynQueryOptions.SettingsCollection + "\\" + RoslynQueryOptions.BridgeDisabledProperty])]
public sealed class RoslynQueryPackage : AsyncPackage
{
    public const string PackageGuidString = "943e02ac-bfd8-4648-9948-4b7d144f6a2d";

    private const string CSharpEditorContextGuidString = "c6829cab-589c-4037-b587-0ac4a1bb2aa8";
    private const string BridgeAutoStartContextGuidString = "69516635-5dc6-44bc-b879-718e1f2216f0";

    public static readonly Guid CommandSet = new Guid("7acc99ae-d964-461e-94d9-9ef567e60889");
    public const int ShowToolWindowCommandId = 0x0100;
    public const int ShowReferenceGraphCommandId = 0x0101;
    public const int ViewReferenceGraphCommandId = 0x0102;
    public const int BridgeToggleCommandId = 0x0210;
    public const int BridgeRestartCommandId = 0x0211;
    public const int BridgeLogCommandId = 0x0212;
    public const int CopyCliPathCommandId = 0x0213;

    private AgentBridge _bridge;

    /// <summary>Set before anything can reach a tool window: opening one force-loads this package first.</summary>
    public static RoslynQueryPackage Instance { get; private set; }

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        Instance = this;
        await base.InitializeAsync(cancellationToken, progress).ConfigureAwait(false);
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        if (await GetServiceAsync(typeof(IMenuCommandService)) is not OleMenuCommandService commands) return;

        commands.AddCommand(new MenuCommand(ShowToolWindow, new CommandID(CommandSet, ShowToolWindowCommandId)));
        commands.AddCommand(new MenuCommand(ShowReferenceGraph, new CommandID(CommandSet, ShowReferenceGraphCommandId)));

        var viewReferenceGraph = new OleMenuCommand(ViewReferenceGraph, new CommandID(CommandSet, ViewReferenceGraphCommandId));
        viewReferenceGraph.BeforeQueryStatus += OnViewReferenceGraphQueryStatus;
        commands.AddCommand(viewReferenceGraph);

        var toggle = new OleMenuCommand(ToggleBridge, new CommandID(CommandSet, BridgeToggleCommandId));
        toggle.BeforeQueryStatus += (_, _) => toggle.Text = _bridge?.IsRunning == true ? "Disable Agent Bridge" : "Enable Agent Bridge";
        commands.AddCommand(toggle);

        var restart = new OleMenuCommand((_, _) => _bridge?.Restart(), new CommandID(CommandSet, BridgeRestartCommandId));
        restart.BeforeQueryStatus += (_, _) => restart.Visible = _bridge?.IsRunning == true;
        commands.AddCommand(restart);

        commands.AddCommand(new MenuCommand(ShowBridgeLog, new CommandID(CommandSet, BridgeLogCommandId)));
        commands.AddCommand(new MenuCommand(CopyCliPath, new CommandID(CommandSet, CopyCliPathCommandId)));

        // Wherever the CLI shipped to, agents read this one stable file to find it.
        System.Threading.Tasks.Task.Run(BridgeCli.PublishPath).FileAndForget("vs/roslynquery/publishcli");

        var options = (RoslynQueryOptions)GetDialogPage(typeof(RoslynQueryOptions));
        options.SyncBridgeDisabledFlag();
        // An elevated Visual Studio never opens the bridge on its own: any process of the same user could then run code in it.
        if (options.AutoStartAgentBridge && !Elevation.IsElevated())
            await EnsureBridgeAsync(start: true).ConfigureAwait(true);
    }

    private async Task<AgentBridge> EnsureBridgeAsync(bool start)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);

        if (_bridge is null)
        {
            if (await GetServiceAsync(typeof(SComponentModel)) is not IComponentModel componentModel) return null;
            _bridge = new AgentBridge(this, componentModel, typeof(RoslynQueryPackage).Assembly.GetName().Version.ToString());
        }

        if (start && !_bridge.IsRunning) _bridge.Start();
        return _bridge;
    }

    /// <summary>True when the user agrees, or Visual Studio is not elevated.</summary>
    private bool ConfirmStartWhenElevated()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!Elevation.IsElevated()) return true;

        const int yes = 6;
        return VsShellUtilities.ShowMessageBox(this,
            "This Visual Studio runs as administrator. With the agent bridge on, any program running as your Windows user can have this elevated Visual Studio execute C# code on its behalf (through the bridge's token file), which lifts it to administrator rights.\r\n\r\nStart the agent bridge anyway?",
            "Roslyn Query: elevated Visual Studio",
            Microsoft.VisualStudio.Shell.Interop.OLEMSGICON.OLEMSGICON_WARNING,
            Microsoft.VisualStudio.Shell.Interop.OLEMSGBUTTON.OLEMSGBUTTON_YESNO,
            Microsoft.VisualStudio.Shell.Interop.OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_SECOND) == yes;
    }

    private void ToggleBridge(object sender, EventArgs e) => JoinableTaskFactory.RunAsync(async () =>
    {
        var options = (RoslynQueryOptions)GetDialogPage(typeof(RoslynQueryOptions));

        if (_bridge?.IsRunning == true)
        {
            _bridge.Stop();
            options.AutoStartAgentBridge = false;
        }
        else
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!ConfirmStartWhenElevated()) return;

            var bridge = await EnsureBridgeAsync(start: true);
            if (bridge is null) return;
            options.AutoStartAgentBridge = true;
        }

        options.SaveSettingsToStorage();
    }).FileAndForget("vs/roslynquery/togglebridge");

    private void ShowBridgeLog(object sender, EventArgs e) => JoinableTaskFactory.RunAsync(async () =>
    {
        var bridge = await EnsureBridgeAsync(start: false);
        bridge?.ShowLog();
    }).FileAndForget("vs/roslynquery/bridgelog");

    private void CopyCliPath(object sender, EventArgs e) => JoinableTaskFactory.RunAsync(async () =>
    {
        await TaskScheduler.Default;
        var path = BridgeCli.Install();
        await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);

        if (path is null)
        {
            VsShellUtilities.ShowMessageBox(this, "This build of the extension does not bundle the roslynquery CLI (it is added by the release build).", "Roslyn Query",
                Microsoft.VisualStudio.Shell.Interop.OLEMSGICON.OLEMSGICON_INFO, Microsoft.VisualStudio.Shell.Interop.OLEMSGBUTTON.OLEMSGBUTTON_OK, Microsoft.VisualStudio.Shell.Interop.OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            return;
        }

        Clipboard.SetText(path);
    }).FileAndForget("vs/roslynquery/copyclipath");

    protected override void Dispose(bool disposing)
    {
        if (disposing) _bridge?.Dispose();
        base.Dispose(disposing);
    }

    private void ShowToolWindow(object sender, EventArgs e) => JoinableTaskFactory.RunAsync(async () =>
    {
        var window = await ShowToolWindowAsync(typeof(QueryToolWindow), 0, create: true, cancellationToken: DisposalToken);
        if (window?.Frame is null) throw new NotSupportedException("Cannot create the Roslyn Query tool window.");
    }).FileAndForget("vs/roslynquery/showtoolwindow");

    private void ShowReferenceGraph(object sender, EventArgs e) => JoinableTaskFactory.RunAsync(async () =>
    {
        await ShowReferenceGraphWindowAsync();
    }).FileAndForget("vs/roslynquery/showreferencegraph");

    private void OnViewReferenceGraphQueryStatus(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var command = (OleMenuCommand)sender;
        var active = ScopeResolver.GetActiveContext(this);

        command.Visible = true;
        command.Enabled = active != null
            && ".cs".Equals(Path.GetExtension(active.FilePath), StringComparison.OrdinalIgnoreCase);
    }

    private void ViewReferenceGraph(object sender, EventArgs e) => JoinableTaskFactory.RunAsync(async () =>
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);

        var active = ScopeResolver.GetActiveContext(this);
        var componentModel = await GetServiceAsync(typeof(SComponentModel)) as IComponentModel;
        var workspace = componentModel?.GetService<VisualStudioWorkspace>();

        var window = await ShowReferenceGraphWindowAsync();

        if (workspace is null)
        {
            window.Control.SetErrorMessage("No Roslyn workspace is available. Open a solution and try again.");
            return;
        }

        var solution = workspace.CurrentSolution;

        await TaskScheduler.Default;
        var symbol = await SymbolResolver.ResolveAtCaretAsync(solution, active, DisposalToken).ConfigureAwait(false);

        await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);

        if (symbol is null)
            window.Control.SetErrorMessage("There is no method, constructor, property, field, event or type at the caret.");
        else
            window.Control.AddRoot(symbol, solution);
    }).FileAndForget("vs/roslynquery/viewreferencegraph");

    private async Task<ReferenceGraphToolWindow> ShowReferenceGraphWindowAsync()
    {
        var window = await ShowToolWindowAsync(
            typeof(ReferenceGraphToolWindow), 0, create: true, cancellationToken: DisposalToken) as ReferenceGraphToolWindow;

        if (window?.Frame is null) throw new NotSupportedException("Cannot create the Reference Graph tool window.");

        return window;
    }
}
