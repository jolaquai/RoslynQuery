using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;

using IServiceProvider = System.IServiceProvider;

namespace RoslynQuery.Query;

// The only part of ScopeResolver that needs the VS shell, split out so the headless CLI can compile the rest.
internal static partial class ScopeResolver
{
    /// <summary>Reads the last active code view. Main thread only.</summary>
    public static ActiveContext GetActiveContext(IServiceProvider serviceProvider)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var manager = serviceProvider.GetService(typeof(SVsTextManager)) as IVsTextManager;
        if (manager is null) return null;
        if (ErrorHandler.Failed(manager.GetActiveView(0, null, out var view)) || view is null) return null;
        if (ErrorHandler.Failed(view.GetCaretPos(out var line, out var column))) return null;
        if (ErrorHandler.Failed(view.GetBuffer(out var buffer)) || buffer is null) return null;

        if (buffer is not IPersistFileFormat persist) return null;
        if (ErrorHandler.Failed(persist.GetCurFile(out var path, out _)) || string.IsNullOrEmpty(path)) return null;

        return new ActiveContext { FilePath = path, Line = line, Column = column };
    }
}
