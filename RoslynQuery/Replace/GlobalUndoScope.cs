using System;
using System.Runtime.InteropServices;

using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.TextManager.Interop;

using IServiceProvider = System.IServiceProvider;

namespace RoslynQuery.Replace;

/// <summary>
/// Groups a multi-file Apply into one Ctrl+Z. Mirrors Roslyn's
/// <c>GlobalUndoServiceFactory.WorkspaceUndoTransaction</c>, whose <c>IGlobalUndoService</c> is
/// internal to Microsoft.CodeAnalysis.EditorFeatures and therefore unreachable from here.
/// Opening the linked undo is not enough on its own: a buffer only joins the linked group if it is
/// touched through the editor while the transaction is open, so every document about to change has
/// to be enrolled via <see cref="AddDocument"/> first. Main thread only.
/// </summary>
internal sealed class GlobalUndoScope : IDisposable
{
    private sealed class NoOpUndoPrimitive : ITextUndoPrimitive
    {
        public ITextUndoTransaction Parent { get; set; }
        public bool CanRedo => true;
        public bool CanUndo => true;
        public void Do() { }
        public void Undo() { }
        public bool CanMerge(ITextUndoPrimitive older) => true;
        public ITextUndoPrimitive Merge(ITextUndoPrimitive older) => older;
    }

    private readonly IServiceProvider _serviceProvider;
    private readonly IVsLinkedUndoTransactionManager _undoManager;
    private readonly ITextUndoHistoryRegistry _undoHistoryRegistry;
    private readonly IVsEditorAdaptersFactoryService _adapterFactory;
    private readonly string _description;
    private bool _alive;

    private GlobalUndoScope(
        IServiceProvider serviceProvider,
        IVsLinkedUndoTransactionManager undoManager,
        ITextUndoHistoryRegistry undoHistoryRegistry,
        IVsEditorAdaptersFactoryService adapterFactory,
        string description)
    {
        _serviceProvider = serviceProvider;
        _undoManager = undoManager;
        _undoHistoryRegistry = undoHistoryRegistry;
        _adapterFactory = adapterFactory;
        _description = description;
        _alive = true;
    }

    /// <summary>
    /// Best effort: returns null when the shell services are unavailable, in which case the caller
    /// still applies its changes, just with one undo step per file.
    /// </summary>
    public static GlobalUndoScope Open(IServiceProvider serviceProvider, IComponentModel componentModel, string description)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (serviceProvider is null) return null;

        var undoManager = serviceProvider.GetService(typeof(SVsLinkedUndoTransactionManager)) as IVsLinkedUndoTransactionManager;
        if (undoManager is null) return null;

        var registry = componentModel?.GetService<ITextUndoHistoryRegistry>();
        var adapters = componentModel?.GetService<IVsEditorAdaptersFactoryService>();

        if (!ErrorHandler.Succeeded(undoManager.OpenLinkedUndo((uint)LinkedTransactionFlags2.mdtGlobal, description)))
            return null;

        return new GlobalUndoScope(serviceProvider, undoManager, registry, adapters, description);
    }

    /// <summary>
    /// Enrols one document's buffer in the open linked undo. Must run before the edit lands.
    /// </summary>
    public void AddDocument(Workspace workspace, DocumentId id)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!_alive) return;

        var filePath = workspace?.CurrentSolution.GetDocument(id)?.FilePath;
        if (string.IsNullOrEmpty(filePath)) return;

        try
        {
            var buffer = TryGetOpenBuffer(filePath);
            if (buffer != null)
            {
                // Already-open buffers were opened before this transaction, so nothing flagged them
                // as participants. An empty transaction on their history does.
                var history = _undoHistoryRegistry?.RegisterHistory(buffer);
                if (history is null) return;

                using (var transaction = history.CreateTransaction(_description))
                {
                    transaction.AddUndo(new NoOpUndoPrimitive());
                    transaction.Complete();
                }
            }
            else
            {
                OpenAndCloseInvisibly(filePath);
            }
        }
        catch (Exception)
        {
            // Enrolment is an undo-quality concern; failing it must not abort the Apply itself.
        }
    }

    private Microsoft.VisualStudio.Text.ITextBuffer TryGetOpenBuffer(string filePath)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_adapterFactory is null) return null;
        if (!VsShellUtilities.IsDocumentOpen(
                _serviceProvider, filePath, VSConstants.LOGVIEWID_Any, out _, out _, out var frame) || frame is null)
            return null;

        if (!ErrorHandler.Succeeded(frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocData, out var docData)))
            return null;

        return docData is IVsTextBuffer vsBuffer ? _adapterFactory.GetDocumentBuffer(vsBuffer) : null;
    }

    private void OpenAndCloseInvisibly(string filePath)
    {
        // A closed file's buffer joins the group purely by being opened inside the transaction, so
        // this is opened and released again immediately; the flag outlives the invisible editor.
        ThreadHelper.ThrowIfNotOnUIThread();

        var manager = _serviceProvider.GetService(typeof(SVsInvisibleEditorManager)) as IVsInvisibleEditorManager;
        if (manager is null) return;

        IVsInvisibleEditor editor = null;
        try
        {
            if (!ErrorHandler.Succeeded(manager.RegisterInvisibleEditor(
                    filePath, null, (uint)_EDITORREGFLAGS.RIEF_ENABLECACHING, null, out editor)))
                return;
        }
        finally
        {
            if (editor != null && Marshal.IsComObject(editor))
                Marshal.ReleaseComObject(editor);
        }
    }

    public void Commit()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!_alive) return;
        _alive = false;

        // UNDO_E_CLIENTABORT means the shell already tore the transaction down for us.
        var hr = _undoManager.CloseLinkedUndo();
        if (hr != VSConstants.UNDO_E_CLIENTABORT)
            ErrorHandler.ThrowOnFailure(hr);
    }

    public void Dispose()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!_alive) return;
        _alive = false;

        // Reached only when Apply threw: rolls the partially applied files back as one unit.
        _undoManager.AbortLinkedUndo();
    }
}
