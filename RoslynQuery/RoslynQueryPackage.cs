using System;
using System.ComponentModel.Design;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

using RoslynQuery.Query;
using RoslynQuery.ReferenceGraph;
using RoslynQuery.ToolWindow;

using Task = System.Threading.Tasks.Task;

namespace RoslynQuery;

[Guid(PackageGuidString)]
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[InstalledProductRegistration("#110", "#112", "0.3.0")]
[ProvideMenuResource("Menus.ctmenu", 1)]
// Docked with the Error List by default: the results grid wants the same horizontal space.
[ProvideToolWindow(typeof(QueryToolWindow), Style = VsDockStyle.Tabbed, Window = "{D78612C7-9962-4B83-95D9-268046DAD23A}")]
[ProvideToolWindow(typeof(ReferenceGraphToolWindow), Style = VsDockStyle.Tabbed, Window = "{D78612C7-9962-4B83-95D9-268046DAD23A}")]
public sealed class RoslynQueryPackage : AsyncPackage
{
    public const string PackageGuidString = "943e02ac-bfd8-4648-9948-4b7d144f6a2d";

    public static readonly Guid CommandSet = new Guid("7acc99ae-d964-461e-94d9-9ef567e60889");
    public const int ShowToolWindowCommandId = 0x0100;
    public const int ShowReferenceGraphCommandId = 0x0101;
    public const int ViewReferenceGraphCommandId = 0x0102;

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await base.InitializeAsync(cancellationToken, progress).ConfigureAwait(false);
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        if (!(await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commands)) return;

        commands.AddCommand(new MenuCommand(ShowToolWindow, new CommandID(CommandSet, ShowToolWindowCommandId)));
        commands.AddCommand(new MenuCommand(ShowReferenceGraph, new CommandID(CommandSet, ShowReferenceGraphCommandId)));

        var viewReferenceGraph = new OleMenuCommand(ViewReferenceGraph, new CommandID(CommandSet, ViewReferenceGraphCommandId));
        viewReferenceGraph.BeforeQueryStatus += OnViewReferenceGraphQueryStatus;
        commands.AddCommand(viewReferenceGraph);
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

    /// <summary>
    /// Cheap and synchronous on purpose: this runs every time the editor's context menu opens, so it
    /// only asks whether there is an active C# view, never what the caret is actually sitting on.
    /// </summary>
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

        // Binding the caret's symbol is semantic work; it does not belong on the UI thread.
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
