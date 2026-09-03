using System;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

using RoslynQuery.Mcp;
using RoslynQuery.Options;
using RoslynQuery.Query;
using RoslynQuery.ReferenceGraph;
using RoslynQuery.ToolWindow;

using Task = System.Threading.Tasks.Task;

namespace RoslynQuery;

[Guid(PackageGuidString)]
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[InstalledProductRegistration("#110", "#112", "0.3.0")]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideToolWindow(typeof(QueryToolWindow), Style = VsDockStyle.Tabbed, Window = "{D78612C7-9962-4B83-95D9-268046DAD23A}")]
[ProvideToolWindow(typeof(ReferenceGraphToolWindow), Style = VsDockStyle.Tabbed, Window = "{D78612C7-9962-4B83-95D9-268046DAD23A}")]
[ProvideOptionPage(typeof(RoslynQueryOptions), "RoslynQuery", "General", 0, 0, true)]
// Without this, "View Reference Graph" stays greyed out until a window is opened by hand: nothing
// else loads the package, so its BeforeQueryStatus never runs.
[ProvideAutoLoad(CSharpEditorContextGuidString, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideUIContextRule(
    CSharpEditorContextGuidString,
    name: "C# editor active",
    expression: "CSharp",
    termNames: ["CSharp"],
    termValues: ["ActiveEditorContentType:CSharp"])]
public sealed class RoslynQueryPackage : AsyncPackage
{
    public const string PackageGuidString = "943e02ac-bfd8-4648-9948-4b7d144f6a2d";

    private const string CSharpEditorContextGuidString = "c6829cab-589c-4037-b587-0ac4a1bb2aa8";

    public static readonly Guid CommandSet = new Guid("7acc99ae-d964-461e-94d9-9ef567e60889");
    public const int ShowToolWindowCommandId = 0x0100;
    public const int ShowReferenceGraphCommandId = 0x0101;
    public const int ViewReferenceGraphCommandId = 0x0102;

    /// <summary>Set before anything can reach a tool window: opening one force-loads this package first.</summary>
    public static RoslynQueryPackage Instance { get; private set; }

    // Fixed for now - see the design doc's open "multi-instance discovery" question. Two VS windows
    // on this machine at once will fight over this port; only the pipe name is already per-instance.
    private const int McpBridgePort = 5050;

    private PipeHost _mcpPipeHost;
    private BrokerProcess _mcpBrokerProcess;

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

        try
        {
            await StartMcpBridgeAsync(cancellationToken);
        }
        catch (Exception ex) when (!ErrorHandler.IsCriticalException(ex))
        {
            // Best-effort: the Search/Replace tool window works fine without the MCP bridge up.
            Debug.WriteLine($"RoslynQuery: MCP bridge failed to start: {ex}");
        }
    }

    /// <summary>
    /// Starts the pipe host and spawns the broker so Search is reachable over MCP without the tool
    /// window ever having been opened. A no-op, not an error, when no workspace is available yet
    /// (no solution open) or the broker executable isn't deployed (see BrokerProcess.Start).
    /// </summary>
    private async Task StartMcpBridgeAsync(CancellationToken cancellationToken)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        var componentModel = await GetServiceAsync(typeof(SComponentModel)) as IComponentModel;
        var workspace = componentModel?.GetService<VisualStudioWorkspace>();
        if (workspace is null) return;

        var pipeName = $"RoslynQuery.{Process.GetCurrentProcess().Id}";

        _mcpPipeHost = new PipeHost(pipeName, workspace);
        _mcpPipeHost.Start();

        _mcpBrokerProcess = new BrokerProcess();
        _mcpBrokerProcess.Start(pipeName, McpBridgePort);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _mcpBrokerProcess?.Dispose();
            _mcpPipeHost?.Dispose();
        }

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
