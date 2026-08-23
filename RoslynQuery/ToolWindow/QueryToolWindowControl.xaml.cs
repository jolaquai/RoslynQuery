using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Threading;

using RoslynQuery.Editor;
using RoslynQuery.Navigation;
using RoslynQuery.Query;
using RoslynQuery.Replace;

namespace RoslynQuery.ToolWindow;

public partial class QueryToolWindowControl : UserControl
{
    private sealed class Choice<T>(string display, T value)
    {
        public string Display { get; } = display;
        public T Value { get; } = value;

        public override string ToString() => Display;
    }

    // Comment, not code: block form can't sit alongside the expression form it's illustrating in one
    // compiled body, and the expression form's own semicolon-free single line would otherwise be a
    // dangling statement missing its terminator (CS1002).
    internal const string DefaultQueryBoxContent = """
        // n.IsKind(SyntaxKind.IfStatement) is the equivalent expression form; block form lets you
        // use statements or control flow constructs:
        if (n is IfStatementSyntax iss && iss.Statement is BlockSyntax b)
            return b.Statements.Count == 1;
        return false;
        """;
    private readonly ObservableCollection<QueryHit> _hits = [];
    private readonly ObservableCollection<CachedPredicateItem> _cachedPredicates = [];
    private readonly ObservableCollection<ReplacementItem> _replacements = [];

    private IComponentModel _componentModel;
    private VisualStudioWorkspace _workspace;
    private IPredicateInput _searchInput;
    private IPredicateInput _replacementInput;
    private CancellationTokenSource _cancellation;
    private bool _initialized;
    private double _sidebarWidth = 220;

    // Weak: a Solution roots its compilations, and pinning the snapshot a run used would keep the
    // whole thing alive for as long as the results are on screen. If it is gone, spans are used
    // as recorded, which is only wrong if the user edited since the run. Replace reuses this same
    // snapshot and the same _hits: Generate Previews re-runs the shared Find query itself, it does
    // not require a prior Search-tab run.
    private WeakReference<Solution> _ranAgainst;

    public QueryToolWindowControl()
    {
        InitializeComponent();

        Results.ItemsSource = _hits;
        CachedPredicates.ItemsSource = _cachedPredicates;
        ReplaceResults.ItemsSource = _replacements;
        Loaded += OnLoaded;
    }

    private TargetKind CurrentTarget => ((Choice<TargetKind>)TargetCombo.SelectedItem)?.Value ?? TargetKind.SyntaxNode;

    private ScopeKind CurrentScope => ((Choice<ScopeKind>)ScopeCombo.SelectedItem)?.Value ?? ScopeKind.Solution;

    private int CurrentCap => ((Choice<int>)CapCombo.SelectedItem)?.Value ?? 5000;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;

        ThreadHelper.ThrowIfNotOnUIThread();

        TargetCombo.ItemsSource = new[]
        {
            new Choice<TargetKind>("SyntaxNode", TargetKind.SyntaxNode),
            new Choice<TargetKind>("SyntaxToken", TargetKind.SyntaxToken),
            new Choice<TargetKind>("IOperation", TargetKind.Operation)
        };
        TargetCombo.SelectedIndex = 0;

        ScopeCombo.ItemsSource = new[]
        {
            new Choice<ScopeKind>("Containing member", ScopeKind.ContainingMember),
            new Choice<ScopeKind>("Containing type", ScopeKind.ContainingType),
            new Choice<ScopeKind>("Current document", ScopeKind.Document),
            new Choice<ScopeKind>("Current project", ScopeKind.Project),
            new Choice<ScopeKind>("Solution", ScopeKind.Solution)
        };
        ScopeCombo.SelectedIndex = 2;

        CapCombo.ItemsSource = new[]
        {
            new Choice<int>("1 000", 1000),
            new Choice<int>("5 000", 5000),
            new Choice<int>("20 000", 20000),
            new Choice<int>("100 000", 100000)
        };
        CapCombo.SelectedIndex = 1;

        _componentModel = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel;
        _workspace = _componentModel?.GetService<VisualStudioWorkspace>();

        _searchInput = PredicateInputFactory.Create(_componentModel, out var diagnostic);
        PredicateHost.Content = _searchInput.Element;
        _searchInput.Target = CurrentTarget;
        _searchInput.Text = DefaultQueryBoxContent;
        // Switches tabs first: running against stale Replace previews from a since-changed query is
        // exactly the confusion this is meant to avoid, and Run() itself clears them either way.
        _searchInput.SubmitRequested += (s, args) =>
        {
            MainTabs.SelectedIndex = 0;
            Run();
        };

        _replacementInput = PredicateInputFactory.Create(_componentModel, out var replacementDiagnostic);
        ReplacementHost.Content = _replacementInput.Element;
        _replacementInput.Target = CurrentTarget;
        _replacementInput.SubmitRequested += (s, args) => GeneratePreview();

        UpdateSignature();
        UpdateReplaceSignature();
        UpdateReplaceAvailability();

        if (_workspace is null) SetError("No Roslyn workspace is available. Open a solution and reopen this window.");
        else if (diagnostic != null) SetError(diagnostic);
        else if (replacementDiagnostic != null) SetError(replacementDiagnostic);

        StatusText.Text = "Ctrl+Enter runs, Enter/Shift+Enter is a newline";

        // The compiler cache is process-lifetime and static, so a reopened or second tool window
        // instance can have entries in it before this instance ever runs anything itself.
        RefreshCachedPredicates();

        // Nothing in the window is focusable-by-default in a useful place, so without this the
        // first keystroke goes to whatever the shell last focused. Input priority: the host has to
        // finish arranging before the view can take focus.
#pragma warning disable VSTHRD001, VSTHRD110
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => _searchInput.FocusInput()));
#pragma warning restore VSTHRD001, VSTHRD110
    }

    private void OnTargetChanged(object sender, SelectionChangedEventArgs e)
    {
        // Populating the combos in OnLoaded raises SelectionChanged before the input exists.
        if (_searchInput is null) return;

        _searchInput.Target = CurrentTarget;
        _replacementInput.Target = CurrentTarget;
        UpdateSignature();
        UpdateReplaceSignature();
        UpdateReplaceAvailability();
    }

    private void OnRunClick(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        Run();
    }

    private void OnStopClick(object sender, RoutedEventArgs e) => _cancellation?.Cancel();

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (Results.SelectedItem is not QueryHit hit || _workspace is null) return;
        NavigateTo(hit.DocumentId, hit.Span);
    }

    private void OnReplaceResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (ReplaceResults.SelectedItem is not ReplacementItem item || _workspace is null) return;
        NavigateTo(item.Hit.DocumentId, item.Hit.Span);
    }

    private void NavigateTo(DocumentId documentId, Microsoft.CodeAnalysis.Text.TextSpan span)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        // VSSDK007: a WPF event handler has nothing to await into; FileAndForget is the terminus.
#pragma warning disable VSSDK007
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            Solution ranAgainst = null;
            _ranAgainst?.TryGetTarget(out ranAgainst);

            await TaskScheduler.Default;
            var target = await SpanMapper
                .ResolveAsync(ranAgainst, _workspace.CurrentSolution, documentId, span, CancellationToken.None)
                .ConfigureAwait(false);

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            SetError(DocumentNavigator.Navigate(ServiceProvider.GlobalProvider, target));
        }).FileAndForget("vs/roslynquery/navigate");
#pragma warning restore VSSDK007
    }

    private void OnCachedPredicateDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (CachedPredicates.SelectedItem is not CachedPredicateItem item) return;

        // TargetCombo is populated in OnLoaded in TargetKind declaration order, so the index and
        // the enum value coincide - no lookup needed. Scope is deliberately left as the user has
        // it: it has nothing to do with which predicate is running.
        // Pretty, not Display: the latter is truncated for the list and would restore a fragment.
        TargetCombo.SelectedIndex = (int)item.Kind;
        _searchInput.Text = item.Pretty;
        Run();
    }

    private void OnToggleSidebarClick(object sender, RoutedEventArgs e)
    {
        var src = System.Runtime.CompilerServices.Unsafe.As<Button>(sender);
        var collapsed = SidebarColumn.Width.Value == 0;
        if (collapsed)
        {
            SidebarColumn.MinWidth = 140;
            SidebarColumn.Width = new GridLength(_sidebarWidth);
            SidebarSplitterColumn.Width = GridLength.Auto;
            SidebarSplitter.Visibility = Visibility.Visible;
            SidebarPane.Visibility = Visibility.Visible;
            src.Content = "History <";
        }
        else
        {
            _sidebarWidth = SidebarColumn.Width.Value > 0 ? SidebarColumn.Width.Value : _sidebarWidth;
            // MinWidth is a hard floor Width can't override: left at 140 while "collapsed" it would
            // pin the column open at 140px of dead space instead of actually closing it.
            SidebarColumn.MinWidth = 0;
            SidebarColumn.Width = new GridLength(0);
            SidebarSplitterColumn.Width = new GridLength(0);
            SidebarSplitter.Visibility = Visibility.Collapsed;
            SidebarPane.Visibility = Visibility.Collapsed;
            src.Content = "History >";
        }
    }

    private void RefreshCachedPredicates()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        _cachedPredicates.Clear();
        foreach (var (kind, mode, text) in PredicateCompiler.Snapshot())
            _cachedPredicates.Add(new CachedPredicateItem(kind, mode, text));
    }

    private void UpdateSignature() => SignatureText.Text = PredicateTemplate.Signature(CurrentTarget);

    private void UpdateReplaceSignature() =>
        ReplaceSignatureText.Text = CurrentTarget == TargetKind.Operation ? string.Empty : ReplaceTemplate.Signature(CurrentTarget);

    /// <summary>IOperation matches have nothing to be structurally replaced with - see ReplaceTemplate's own guard.</summary>
    private void UpdateReplaceAvailability()
    {
        var unavailable = CurrentTarget == TargetKind.Operation;
        ReplaceUnavailableText.Visibility = unavailable ? Visibility.Visible : Visibility.Collapsed;
        ReplaceBody.Visibility = unavailable ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetError(string message)
    {
        ErrorText.Text = message ?? string.Empty;
        ErrorText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetReplaceError(string message)
    {
        ReplaceErrorText.Text = message ?? string.Empty;
        ReplaceErrorText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Run()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_workspace is null)
        {
            SetError("No Roslyn workspace is available. Open a solution and reopen this window.");
            return;
        }

        _cancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;

        var target = CurrentTarget;
        var scope = CurrentScope;
        var expression = _searchInput.Text;
        var includeGenerated = GeneratedCheckBox.IsChecked == true;
        var cap = CurrentCap;

        _hits.Clear();
        _replacements.Clear();
        ApplySelectedButton.IsEnabled = false;
        SetError(null);
        StatusText.Text = "Running...";
        StopButton.IsEnabled = true;
        ReplaceStopButton.IsEnabled = true;
        RunButton.IsEnabled = false;
        GeneratePreviewButton.IsEnabled = false;

#pragma warning disable VSSDK007
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await RunCoreAsync(target, scope, expression, includeGenerated, cap, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                StatusText.Text = "Cancelled.";
            }
            catch (Exception ex)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                StatusText.Text = "Failed.";
                SetError(ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                StopButton.IsEnabled = false;
                ReplaceStopButton.IsEnabled = false;
                RunButton.IsEnabled = true;
                GeneratePreviewButton.IsEnabled = true;
                RefreshCachedPredicates();
                if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
                cancellation.Dispose();
            }
        }).FileAndForget("vs/roslynquery/run");
#pragma warning restore VSSDK007
    }

    private async Task RunCoreAsync(TargetKind target, ScopeKind scope, string expression, bool includeGenerated, int cap, CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var active = ScopeResolver.GetActiveContext(ServiceProvider.GlobalProvider);
        var solution = _workspace.CurrentSolution;
        _ranAgainst = new WeakReference<Solution>(solution);

        await TaskScheduler.Default;

        Delegate predicate;
        try
        {
            predicate = PredicateCompiler.Compile(target, expression);
        }
        catch (PredicateCompilationException ex)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            StatusText.Text = "The predicate did not compile.";
            SetError(ex.Message);
            return;
        }

        var units = await ScopeResolver.ResolveAsync(solution, scope, active, includeGenerated, cancellationToken).ConfigureAwait(false);
        if (units.Count == 0)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            StatusText.Text = "Nothing in scope. Put the caret in a C# file and try again.";
            return;
        }

        // Background priority on purpose: batches arrive from several scanning threads and must
        // coalesce behind input, which a JoinableTaskFactory switch cannot express.
#pragma warning disable VSTHRD001, VSTHRD110
        void OnBatch(IReadOnlyList<QueryHit> batch) =>
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                foreach (var hit in batch) _hits.Add(hit);
            }));
#pragma warning restore VSTHRD001, VSTHRD110

        var outcome = await QueryEngine
            .RunAsync(units, target, expression, predicate, cap, OnBatch, cancellationToken)
            .ConfigureAwait(false);

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        StatusText.Text = string.Format(
            "{0:N0} match{1} from {2:N0} examined across {3:N0} document{4} in {5:N0} ms{6} | predicate cache: {7:N0} ({8:N1} KB)",
            outcome.Matched,
            outcome.Matched == 1 ? string.Empty : "es",
            outcome.Examined,
            outcome.Documents,
            outcome.Documents == 1 ? string.Empty : "s",
            outcome.Elapsed.TotalMilliseconds,
            outcome.Truncated ? " (capped)" : string.Empty,
            PredicateCompiler.CachedExpressionCount,
            PredicateCompiler.TotalEmittedBytes / 1024.0);

        if (outcome.Errors > 0) SetError(string.Format("{0:N0} predicate error{1}. First: {2}", outcome.Errors, outcome.Errors == 1 ? string.Empty : "s", outcome.FirstError));
    }

    private void OnGeneratePreviewClick(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        GeneratePreview();
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        // Warned rows keep their box disabled and unchecked in the DataTemplate; setting Included
        // from code here would silently override that and re-introduce the "checked, but Apply skips
        // it anyway" case this is meant to prevent.
        foreach (var item in _replacements)
        {
            if (item.Warning is null) item.Included = true;
        }
    }

    private void OnSelectNoneClick(object sender, RoutedEventArgs e)
    {
        foreach (var item in _replacements) item.Included = false;
    }

    /// <summary>
    /// Re-runs the shared Find query and generates a replacement preview per hit in one action - a
    /// user reaching for Replace should not first have to go run Search. Shares the Stop button and
    /// cancellation with Run(), since this does everything Run() does plus the replacement step.
    /// </summary>
    private void GeneratePreview()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_workspace is null)
        {
            SetReplaceError("No Roslyn workspace is available. Open a solution and reopen this window.");
            return;
        }
        if (CurrentTarget == TargetKind.Operation)
        {
            SetReplaceError("Replace isn't available for IOperation matches.");
            return;
        }

        _cancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;

        var target = CurrentTarget;
        var scope = CurrentScope;
        var findExpression = _searchInput.Text;
        var replacementExpression = _replacementInput.Text;
        var includeGenerated = GeneratedCheckBox.IsChecked == true;
        var cap = CurrentCap;

        _hits.Clear();
        _replacements.Clear();
        SetError(null);
        SetReplaceError(null);
        StatusText.Text = "Running...";
        ReplaceStatusText.Text = "Generating...";
        StopButton.IsEnabled = true;
        ReplaceStopButton.IsEnabled = true;
        RunButton.IsEnabled = false;
        GeneratePreviewButton.IsEnabled = false;
        ApplySelectedButton.IsEnabled = false;

#pragma warning disable VSSDK007
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await RunCoreAsync(target, scope, findExpression, includeGenerated, cap, cancellation.Token);
                if (_hits.Count == 0)
                {
                    ReplaceStatusText.Text = "No matches.";
                    return;
                }

                Solution ranAgainst = null;
                _ranAgainst?.TryGetTarget(out ranAgainst);
                if (ranAgainst is null)
                {
                    SetReplaceError("The search snapshot is gone; try again.");
                    return;
                }

                await GeneratePreviewCoreAsync(ranAgainst, [.. _hits], target, replacementExpression);
            }
            catch (OperationCanceledException)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                ReplaceStatusText.Text = "Cancelled.";
            }
            catch (Exception ex)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                ReplaceStatusText.Text = "Failed.";
                SetReplaceError(ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                StopButton.IsEnabled = false;
                ReplaceStopButton.IsEnabled = false;
                RunButton.IsEnabled = true;
                GeneratePreviewButton.IsEnabled = true;
                RefreshCachedPredicates();
                if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
                cancellation.Dispose();
            }
        }).FileAndForget("vs/roslynquery/replacepreview");
#pragma warning restore VSSDK007
    }

    private async Task GeneratePreviewCoreAsync(Solution ranAgainst, QueryHit[] hits, TargetKind target, string replacementExpression)
    {
        await TaskScheduler.Default;

        Delegate replace;
        try
        {
            replace = ReplaceCompiler.Compile(target, replacementExpression);
        }
        catch (PredicateCompilationException ex)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            ReplaceStatusText.Text = "The replacement did not compile.";
            SetReplaceError(ex.Message);
            return;
        }

        var items = await ReplaceEngine.GenerateAsync(ranAgainst, hits, target, replace, CancellationToken.None).ConfigureAwait(false);
        ReplaceEngine.MarkConflicts(items);

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        foreach (var item in items) _replacements.Add(item);

        var included = items.Count(i => i.Included);
        ReplaceStatusText.Text = string.Format(
            "{0:N0} match{1} previewed, {2:N0} selected", items.Count, items.Count == 1 ? string.Empty : "es", included);
        ApplySelectedButton.IsEnabled = included > 0;
    }

    private void OnApplySelectedClick(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        ApplySelected();
    }

    private void ApplySelected()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_workspace is null)
        {
            SetReplaceError("No Roslyn workspace is available.");
            return;
        }

        Solution ranAgainst = null;
        _ranAgainst?.TryGetTarget(out ranAgainst);

        var items = _replacements.ToArray();
        ApplySelectedButton.IsEnabled = false;
        GeneratePreviewButton.IsEnabled = false;
        SetReplaceError(null);
        ReplaceStatusText.Text = "Applying...";

        // Multiple files can change in one Apply; a linked undo transaction makes that one Ctrl+Z
        // instead of one per file. Best-effort: an unavailable service just means per-file undo.
        var undoScope = GlobalUndoScope.Open(ServiceProvider.GlobalProvider, _componentModel, "Replace");

#pragma warning disable VSSDK007
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            var committed = false;
            try
            {
                await ApplySelectedCoreAsync(ranAgainst, items, undoScope);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                undoScope?.Commit();
                committed = true;
            }
            catch (Exception ex)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                ReplaceStatusText.Text = "Failed.";
                SetReplaceError(ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                // Only reached uncommitted when Apply threw, where Dispose aborts the transaction
                // and rolls the partially applied files back together.
                if (!committed) undoScope?.Dispose();
                GeneratePreviewButton.IsEnabled = true;
            }
        }).FileAndForget("vs/roslynquery/replaceapply");
#pragma warning restore VSSDK007
    }

    private async Task ApplySelectedCoreAsync(Solution ranAgainst, ReplacementItem[] items, GlobalUndoScope undoScope)
    {
        // Stays on the UI thread throughout: TryApplyChanges touches live text buffers, and the
        // span-remap/diff work ahead of it is cheap next to a Search scan.
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var workspace = _workspace;
        System.Action<DocumentId> enroll = undoScope is null ? null : id => undoScope.AddDocument(workspace, id);

        var outcome = await ChangeApplier.ApplyAsync(workspace, ranAgainst, items, enroll, CancellationToken.None).ConfigureAwait(true);

        ReplaceStatusText.Text = string.Format("{0:N0} applied, {1:N0} skipped.", outcome.Applied, outcome.Skipped);
        if (outcome.Warnings.Count > 0)
            SetReplaceError(outcome.Warnings[0] + (outcome.Warnings.Count > 1 ? $" (+{outcome.Warnings.Count - 1} more)" : string.Empty));

        if (outcome.Applied > 0)
        {
            // Every remaining span in _hits/_replacements is only as good as the snapshot it was
            // resolved against, which the apply just moved past - clearing avoids showing results
            // that would silently resolve to the wrong place if acted on again.
            _replacements.Clear();
            _hits.Clear();
            ApplySelectedButton.IsEnabled = false;
        }
    }
}
