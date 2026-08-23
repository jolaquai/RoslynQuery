using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.Utilities;

using RoslynQuery.Query;

using VsData = Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;

namespace RoslynQuery.Editor;

internal interface IPredicateInput
{
    UIElement Element { get; }
    string Text { get; set; }
    TargetKind Target { get; set; }

    event EventHandler SubmitRequested;

    void FocusInput();
}

internal static class PredicateInputFactory
{
    public static IPredicateInput Create(IComponentModel componentModel, out string diagnostic)
    {
        try
        {
            var input = new PredicateEditorInput(componentModel);
            diagnostic = null;
            return input;
        }
        catch (Exception ex)
        {
            diagnostic = "IntelliSense unavailable (" + ex.GetType().Name + "); using a plain text box.";
            return new PredicateTextBoxInput();
        }
    }
}

/// <summary>
/// A real WPF editor view over a private content type. The view is not an IVsTextView, so VS's
/// command routing never reaches it: <c>WpfTextView</c> is a bare <c>ContentControl</c> with no
/// <c>OnKeyDown</c> and no <c>OnTextInput</c> of its own, because in a real editor every keystroke
/// arrives as a command through the adapter's IOleCommandTarget. Both the keyboard map and the
/// completion session therefore have to be driven by hand, against <see cref="IEditorOperations"/>;
/// that is still far less machinery than hosting an HWND editor inside a tool window.
/// </summary>
internal sealed class PredicateEditorInput : IPredicateInput
{
    private readonly ITextBuffer _buffer;
    private readonly IWpfTextView _view;
    private readonly IAsyncCompletionBroker _broker;
    private readonly IEditorOperations _operations;
    private readonly ITextUndoHistory _undo;
    private readonly PredicateBufferContext _context = new PredicateBufferContext();

    public PredicateEditorInput(IComponentModel componentModel)
    {
        var contentTypes = componentModel.GetService<IContentTypeRegistryService>();
        var bufferFactory = componentModel.GetService<ITextBufferFactoryService>();
        var editorFactory = componentModel.GetService<ITextEditorFactoryService>();
        var operationsFactory = componentModel.GetService<IEditorOperationsFactoryService>();
        var undoRegistry = componentModel.GetService<ITextUndoHistoryRegistry>();
        _broker = componentModel.GetService<IAsyncCompletionBroker>();

        var contentType = contentTypes.GetContentType(PredicateContentTypes.Name)
            ?? throw new InvalidOperationException("Content type " + PredicateContentTypes.Name + " is not registered.");

        _buffer = bufferFactory.CreateTextBuffer(string.Empty, contentType);
        PredicateBufferContext.Attach(_buffer, _context);

        // Without a registered history the operations run untracked and Ctrl+Z has nothing to undo.
        _undo = undoRegistry?.RegisterHistory(_buffer);

        // Zoomable is deliberately absent: it's what makes the host compose a zoom-control margin at
        // all, and DefaultTextViewHostOptions.ZoomControlId/HorizontalScrollBarId being false is not
        // enough on its own to keep that margin's row from rendering as an empty strip under a
        // one-line input box.
        var roles = editorFactory.CreateTextViewRoleSet(
            PredefinedTextViewRoles.Editable,
            PredefinedTextViewRoles.Interactive,
            PredefinedTextViewRoles.Analyzable);

        _view = editorFactory.CreateTextView(_buffer, roles);
        ConfigureView(_view);
        _operations = operationsFactory.GetEditorOperations(_view);

        var host = editorFactory.CreateTextViewHost(_view, setFocus: false);
        Element = host.HostControl;

        _buffer.Changed += OnBufferChanged;

        // On the host, not the view: these tunnel, so they fire whichever descendant holds focus.
        Element.PreviewKeyDown += OnPreviewKeyDown;
        Element.PreviewTextInput += OnPreviewTextInput;
        Element.PreviewMouseDown += OnPreviewMouseDown;
    }

    public UIElement Element { get; }

    public string Text
    {
        get => _buffer.CurrentSnapshot.GetText();
        set
        {
            using (var edit = _buffer.CreateEdit())
            {
                edit.Replace(0, _buffer.CurrentSnapshot.Length, value ?? string.Empty);
                edit.Apply();
            }
        }
    }

    public TargetKind Target
    {
        get => _context.Target;
        set
        {
            _context.Target = value;
            DismissSession();
        }
    }

    public event EventHandler SubmitRequested;

    public void FocusInput() => _view.VisualElement.Focus();

    private static void ConfigureView(IWpfTextView view)
    {
        view.Options.SetOptionValue(DefaultTextViewHostOptions.LineNumberMarginId, false);
        view.Options.SetOptionValue(DefaultTextViewHostOptions.GlyphMarginId, false);
        view.Options.SetOptionValue(DefaultTextViewHostOptions.SelectionMarginId, false);
        view.Options.SetOptionValue(DefaultTextViewHostOptions.OutliningMarginId, false);
        view.Options.SetOptionValue(DefaultTextViewHostOptions.ChangeTrackingId, false);
        view.Options.SetOptionValue(DefaultTextViewHostOptions.HorizontalScrollBarId, false);
        view.Options.SetOptionValue(DefaultTextViewHostOptions.VerticalScrollBarId, false);
        view.Options.SetOptionValue(DefaultTextViewHostOptions.ZoomControlId, false);
        view.Options.SetOptionValue(DefaultTextViewOptions.WordWrapStyleId, WordWrapStyles.WordWrap);
        view.Options.SetOptionValue(DefaultOptions.ConvertTabsToSpacesOptionId, true);

        // Otherwise the view paints the C# editor's own background over the box's border fill.
        view.Background = Brushes.Transparent;
    }

    private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
    {
        if (e.Changes.Count != 1) return;
        var change = e.Changes[0];
        var point = new SnapshotPoint(e.After, Math.Min(change.NewEnd, e.After.Length));

        if (change.NewLength == 1 && change.OldLength == 0)
        {
            var typed = e.After[change.NewEnd - 1];
            var relevant = char.IsLetterOrDigit(typed) || typed == '_' || typed == '.';
            Trigger(point, typed, relevant ? VsData.CompletionTriggerReason.Insertion : VsData.CompletionTriggerReason.Deletion);
        }
        else if (change.OldLength > 0 && change.NewLength == 0)
        {
            Trigger(point, '\0', VsData.CompletionTriggerReason.Backspace);
        }
    }

    private void Trigger(SnapshotPoint point, char typed, VsData.CompletionTriggerReason reason)
    {
        try
        {
            var trigger = new VsData.CompletionTrigger(reason, point.Snapshot, typed);
            var session = _broker.GetSession(_view);

            // Anything that is not another identifier character ends the word the session was
            // opened on, and the item list for `n.` has nothing to do with the one for `n`.
            if (session != null && !session.IsDismissed && typed != '\0' && !PredicateWord.IsIdentifierChar(typed))
            {
                session.Dismiss();
                session = null;
            }

            if (session != null && !session.IsDismissed &&
                !PredicateWord.At(point.Snapshot, point.Position).Equals(session.ApplicableToSpan.GetSpan(point.Snapshot).Span))
            {
                // ApplicableToSpan is fixed for the life of a session - IAsyncCompletionSessionOperations
                // exposes a setter, but VS throws NotSupportedException on the second assignment. A
                // session opened on `n` that must now cover `n.` has to be re-opened, not re-pointed.
                session.Dismiss();
                session = null;
            }

            if (session is null || session.IsDismissed)
            {
                // Only a fresh insertion or an explicit invoke should open a new list; backspacing
                // past the trigger point must let the session stay closed.
                if (reason != VsData.CompletionTriggerReason.Insertion && reason != VsData.CompletionTriggerReason.Invoke) return;
                session = _broker.TriggerCompletion(_view, trigger, point, CancellationToken.None);
            }

            session?.OpenOrUpdate(trigger, point, CancellationToken.None);
        }
        catch (Exception)
        {
            // A failing completion session must never take the tool window with it.
        }
    }

    private static VsData.CommitBehavior Commit(IAsyncCompletionSession session, char typed)
    {
        var behavior = session.Commit(typed, CancellationToken.None);
        session.Dismiss();
        return behavior;
    }

    /// <summary>
    /// Commits the selected item when the typed character ends it, the way the editor's own command
    /// handler would. Returns true when the commit also consumed the character.
    /// </summary>
    private bool TryCommitOnTypedChar(char typed)
    {
        try
        {
            var session = _broker.GetSession(_view);
            if (session is null || session.IsDismissed) return false;

            // Soft selection means nothing is really picked - after a bare Ctrl+Space, say - and
            // committing on the next keystroke would insert whatever happened to sort first.
            if (session.GetComputedItems(CancellationToken.None).UsesSoftSelection) return false;
            if (!session.ShouldCommit(typed, _view.Caret.Position.BufferPosition, CancellationToken.None)) return false;

            return (Commit(session, typed) & VsData.CommitBehavior.SuppressFurtherTypeCharCommandHandlers) != 0;
        }
        catch (Exception)
        {
            // A failing completion session must never swallow the keystroke.
            return false;
        }
    }

    private void DismissSession()
    {
        var session = _broker.GetSession(_view);
        if (session != null && !session.IsDismissed) session.Dismiss();
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // The host is focusable in its own right, so a click can land there and leave the view -
        // and every key handler below - out of the focus path.
        if (!_view.VisualElement.IsKeyboardFocusWithin) _view.VisualElement.Focus();
    }

    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;

        // Ctrl and Alt chords surface here as control characters; only AltGr carries real text.
        var chord = Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt);
        if (chord != ModifierKeys.None && chord != (ModifierKeys.Control | ModifierKeys.Alt)) return;
        if (e.Text.Length == 1 && char.IsControl(e.Text[0])) return;

        if (e.Text.Length != 1 || !TryCommitOnTypedChar(e.Text[0])) _operations.InsertText(e.Text);
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if ((modifiers & ModifierKeys.Alt) != 0) return;

        var extend = (modifiers & ModifierKeys.Shift) != 0;
        var control = (modifiers & ModifierKeys.Control) != 0;

        var session = _broker.GetSession(_view);
        var active = session != null && !session.IsDismissed;

        // List navigation lives on IAsyncCompletionSessionOperations, not on the session itself.
        var operations = session as IAsyncCompletionSessionOperations;

        switch (e.Key)
        {
            // Always handled. Left alone, Esc is the shell's "give focus back to the document"
            // gesture, so dismissing a completion list threw the user out of the tool window.
            case Key.Escape:
                if (active) session.Dismiss();
                e.Handled = true;
                break;

            // Every caret key must be marked handled even when it does nothing useful: an
            // unhandled arrow falls through to WPF directional navigation, which is what threw
            // focus back onto the last combo box.
            case Key.Down:
                if (active && operations != null) operations.SelectDown();
                else _operations.MoveLineDown(extend);
                e.Handled = true;
                break;
            case Key.Up:
                if (active && operations != null) operations.SelectUp();
                else _operations.MoveLineUp(extend);
                e.Handled = true;
                break;
            case Key.PageDown:
                if (active && operations != null) operations.SelectPageDown();
                else _operations.PageDown(extend);
                e.Handled = true;
                break;
            case Key.PageUp:
                if (active && operations != null) operations.SelectPageUp();
                else _operations.PageUp(extend);
                e.Handled = true;
                break;
            case Key.Left:
                if (control) _operations.MoveToPreviousWord(extend);
                else _operations.MoveToPreviousCharacter(extend);
                e.Handled = true;
                break;
            case Key.Right:
                if (control) _operations.MoveToNextWord(extend);
                else _operations.MoveToNextCharacter(extend);
                e.Handled = true;
                break;
            case Key.Home:
                if (control) _operations.MoveToStartOfDocument(extend);
                else _operations.MoveToHome(extend);
                e.Handled = true;
                break;
            case Key.End:
                if (control) _operations.MoveToEndOfDocument(extend);
                else _operations.MoveToEndOfLine(extend);
                e.Handled = true;
                break;

            case Key.Back:
                if (control) _operations.DeleteWordToLeft();
                else _operations.Backspace();
                e.Handled = true;
                break;
            case Key.Delete:
                if (control) _operations.DeleteWordToRight();
                else _operations.Delete();
                e.Handled = true;
                break;

            // Tab with no session is left alone on purpose: it should walk out of a one-line box.
            case Key.Tab:
                if (active) { Commit(session, '\t'); e.Handled = true; }
                break;
            case Key.Enter:
                if (active) { Commit(session, '\n'); e.Handled = true; }
                else if (control) { SubmitRequested?.Invoke(this, EventArgs.Empty); e.Handled = true; }
                else { _operations.InsertNewLine(); e.Handled = true; }
                break;

            case Key.Space:
                if (control)
                {
                    Trigger(_view.Caret.Position.BufferPosition, '\0', VsData.CompletionTriggerReason.Invoke);
                    e.Handled = true;
                }
                break;

            case Key.A:
                if (control) { _operations.SelectAll(); e.Handled = true; }
                break;
            case Key.C:
                if (control) { _operations.CopySelection(); e.Handled = true; }
                break;
            case Key.X:
                if (control) { _operations.CutSelection(); e.Handled = true; }
                break;
            case Key.V:
                if (control) { _operations.Paste(); e.Handled = true; }
                break;
            case Key.Z:
                if (control) { if (_undo != null && _undo.CanUndo) _undo.Undo(1); e.Handled = true; }
                break;
            case Key.Y:
                if (control) { if (_undo != null && _undo.CanRedo) _undo.Redo(1); e.Handled = true; }
                break;
        }
    }
}

internal sealed class PredicateTextBoxInput : IPredicateInput
{
    private readonly TextBox _textBox;

    public PredicateTextBoxInput()
    {
        _textBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };

        _textBox.PreviewKeyDown += (_, e) =>
        {
            // Plain/Shift+Enter falls through unhandled to AcceptsReturn's own newline insertion.
            if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            SubmitRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        };
    }

    public UIElement Element => _textBox;

    public string Text
    {
        get => _textBox.Text;
        set => _textBox.Text = value ?? string.Empty;
    }

    public TargetKind Target { get; set; }

    public event EventHandler SubmitRequested;

    public void FocusInput() => _textBox.Focus();
}
