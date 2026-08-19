using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;

using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

using RoslynQuery.ToolWindow;

using Task = System.Threading.Tasks.Task;

namespace RoslynQuery;

[Guid(PackageGuidString)]
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[InstalledProductRegistration("#110", "#112", "0.2.0")]
[ProvideMenuResource("Menus.ctmenu", 1)]
// Docked with the Error List by default: the results grid wants the same horizontal space.
[ProvideToolWindow(typeof(QueryToolWindow), Style = VsDockStyle.Tabbed, Window = "{D78612C7-9962-4B83-95D9-268046DAD23A}")]
public sealed class RoslynQueryPackage : AsyncPackage
{
    public const string PackageGuidString = "943e02ac-bfd8-4648-9948-4b7d144f6a2d";

    public static readonly Guid CommandSet = new Guid("7acc99ae-d964-461e-94d9-9ef567e60889");
    public const int ShowToolWindowCommandId = 0x0100;

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await base.InitializeAsync(cancellationToken, progress).ConfigureAwait(false);
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commands)
            commands.AddCommand(new MenuCommand(ShowToolWindow, new CommandID(CommandSet, ShowToolWindowCommandId)));
    }

    private void ShowToolWindow(object sender, EventArgs e) => JoinableTaskFactory.RunAsync(async () =>
    {
        var window = await ShowToolWindowAsync(typeof(QueryToolWindow), 0, create: true, cancellationToken: DisposalToken);
        if (window?.Frame is null) throw new NotSupportedException("Cannot create the Roslyn Query tool window.");
    }).FileAndForget("vs/roslynquery/showtoolwindow");
}
