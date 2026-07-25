using System;

using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

using IServiceProvider = System.IServiceProvider;

namespace RoslynQuery.Navigation;

internal static class DocumentNavigator
{
    /// <summary>
    /// Opens the document and selects the target. Roslyn's own IDocumentNavigationService is
    /// internal, so this goes through the shell instead. Main thread only.
    /// </summary>
    public static string Navigate(IServiceProvider serviceProvider, NavigationTarget target)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (target is null) return "That match no longer exists in the current solution.";
        if (string.IsNullOrEmpty(target.FilePath)) return "This match has no file on disk (source generated).";

        try
        {
            VsShellUtilities.OpenDocument(serviceProvider, target.FilePath, VSConstants.LOGVIEWID_TextView, out _, out _, out var frame);
            if (frame is null) return "Could not open " + target.FilePath + ".";

            ErrorHandler.ThrowOnFailure(frame.Show());

            var view = VsShellUtilities.GetTextView(frame);
            if (view is null) return null;

            ErrorHandler.ThrowOnFailure(view.SetCaretPos(target.Line, target.Column));
            view.SetSelection(target.Line, target.Column, target.EndLine, target.EndColumn);
            view.CenterLines(target.Line, 1);
            return null;
        }
        catch (Exception ex)
        {
            return "Navigation failed: " + ex.Message;
        }
    }
}
