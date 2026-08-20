using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

using RoslynQuery.Editor;
using RoslynQuery.Navigation;
using RoslynQuery.Query;

namespace RoslynQuery.ToolWindow;

public partial class QueryToolWindowControl : UserControl
{
    private sealed class Choice<T>
    {
        public Choice(string display, T value)
        {
            Display = display;
            Value = value;
        }

        public string Display { get; }
        public T Value { get; }

        public override string ToString() => Display;
    }

    private readonly ObservableCollection<QueryHit> _hits = new ObservableCollection<QueryHit>();
    private readonly ObservableCollection<CachedPredicateItem> _cachedPredicates = new ObservableCollection<CachedPredicateItem>();

    private IComponentModel _componentModel;
    private VisualStudioWorkspace _workspace;
    private IPredicateInput _input;
    private CancellationTokenSource _cancellation;
    private bool _initialized;
    private double _sidebarWidth = 220;

    // Weak: a Solution roots its compilations, and pinning the snapshot a run used would keep the
    // whole thing alive for as long as the results are on screen. If it is gone, spans are used
    // as recorded, which is only wrong if the user edited since the run.
    private WeakReference<Solution> _ranAgainst;

    public QueryToolWindowControl()
    {
        InitializeComponent();

        Results.ItemsSource = _hits;
        CachedPredicates.ItemsSource = _cachedPredicates;
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

        _input = PredicateInputFactory.Create(_componentModel, out var diagnostic);
        PredicateHost.Content = _input.Element;
        _input.Target = CurrentTarget;
        _input.Text = "n.IsKind(SyntaxKind.IfStatement)";
        _input.SubmitRequested += (s, args) => Run();

        UpdateSignature();

        if (_workspace is null) SetError("No Roslyn workspace is available. Open a solution and reopen this window.");
        else if (diagnostic != null) SetError(diagnostic);

        StatusText.Text = "Enter runs, Shift+Enter is a newline";

        // The compiler cache is process-lifetime and static, so a reopened or second tool window
        // instance can have entries in it before this instance ever runs anything itself.
        RefreshCachedPredicates();

        // Nothing in the window is focusable-by-default in a useful place, so without this the
        // first keystroke goes to whatever the shell last focused. Input priority: the host has to
        // finish arranging before the view can take focus.
#pragma warning disable VSTHRD001, VSTHRD110
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => _input.FocusInput()));
#pragma warning restore VSTHRD001, VSTHRD110
    }

    private void OnTargetChanged(object sender, SelectionChangedEventArgs e)
    {
        // Populating the combos in OnLoaded raises SelectionChanged before the input exists.
        if (_input is null) return;

        _input.Target = CurrentTarget;
        UpdateSignature();
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

        if (!(Results.SelectedItem is QueryHit hit) || _workspace is null) return;

        // VSSDK007: a WPF event handler has nothing to await into; FileAndForget is the terminus.
#pragma warning disable VSSDK007
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            Solution ranAgainst = null;
            _ranAgainst?.TryGetTarget(out ranAgainst);

            await TaskScheduler.Default;
            var target = await SpanMapper
                .ResolveAsync(ranAgainst, _workspace.CurrentSolution, hit.DocumentId, hit.Span, CancellationToken.None)
                .ConfigureAwait(false);

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            SetError(DocumentNavigator.Navigate(ServiceProvider.GlobalProvider, target));
        }).FileAndForget("vs/roslynquery/navigate");
#pragma warning restore VSSDK007
    }

    private void OnCachedPredicateDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!(CachedPredicates.SelectedItem is CachedPredicateItem item)) return;

        // TargetCombo is populated in OnLoaded in TargetKind declaration order, so the index and
        // the enum value coincide - no lookup needed. Scope is deliberately left as the user has
        // it: it has nothing to do with which predicate is running.
        // Pretty, not Display: the latter is truncated for the list and would restore a fragment.
        TargetCombo.SelectedIndex = (int)item.Kind;
        _input.Text = item.Pretty;
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

    private void SetError(string message)
    {
        ErrorText.Text = message ?? string.Empty;
        ErrorText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
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
        var expression = _input.Text;
        var includeGenerated = GeneratedCheckBox.IsChecked == true;
        var cap = CurrentCap;

        _hits.Clear();
        SetError(null);
        StatusText.Text = "Running...";
        StopButton.IsEnabled = true;
        RunButton.IsEnabled = false;

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
                RunButton.IsEnabled = true;
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
}
