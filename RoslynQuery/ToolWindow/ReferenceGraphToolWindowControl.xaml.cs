using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

using RoslynQuery.Navigation;
using RoslynQuery.Query;
using RoslynQuery.ReferenceGraph;

namespace RoslynQuery.ToolWindow;

public partial class ReferenceGraphToolWindowControl : UserControl
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

    private readonly ObservableCollection<ReferenceGraphNode> _roots = new ObservableCollection<ReferenceGraphNode>();

    private VisualStudioWorkspace _workspace;
    private CancellationTokenSource _cancellation;
    private int _running;
    private bool _initialized;

    // Separate from _initialized: the scope combo raises SelectionChanged while OnLoaded is still
    // populating it, and that must not count as the user changing anything.
    private bool _ready;

    // Weak for the same reason QueryToolWindowControl's is: a Solution roots its compilations, and
    // the tree can sit on screen for hours. If it is gone, spans are used as recorded.
    private WeakReference<Solution> _ranAgainst;

    public ReferenceGraphToolWindowControl()
    {
        InitializeComponent();

        Tree.ItemsSource = _roots;
        Loaded += OnLoaded;
    }

    private ScopeKind CurrentScope => ((Choice<ScopeKind>)ScopeCombo.SelectedItem)?.Value ?? ScopeKind.Project;

    private ReferenceUsageKind CurrentFilter =>
        Flag(InvocationCheck, ReferenceUsageKind.Invocation)
        | Flag(ReadCheck, ReferenceUsageKind.Read)
        | Flag(WriteCheck, ReferenceUsageKind.Write)
        | Flag(ConstructionCheck, ReferenceUsageKind.Construction)
        | Flag(TypeReferenceCheck, ReferenceUsageKind.TypeReference);

    private static ReferenceUsageKind Flag(CheckBox box, ReferenceUsageKind kind) =>
        box.IsChecked == true ? kind : ReferenceUsageKind.None;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;

        ThreadHelper.ThrowIfNotOnUIThread();

        ScopeCombo.ItemsSource = new[]
        {
            new Choice<ScopeKind>("Current document", ScopeKind.Document),
            new Choice<ScopeKind>("Current project", ScopeKind.Project),
            new Choice<ScopeKind>("My solution", ScopeKind.Solution)
        };
        ScopeCombo.SelectedIndex = 1;

        var componentModel = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel;
        _workspace = componentModel?.GetService<VisualStudioWorkspace>();

        if (_workspace is null) SetError("No Roslyn workspace is available. Open a solution and reopen this window.");
        else StatusText.Text = "Right-click a member or type in the editor and choose View Reference Graph.";

        _ready = true;
    }

    /// <summary>
    /// Puts a new root at the top of the list. Every invocation adds one rather than replacing what
    /// is there, so the window keeps a history the Clear button empties.
    /// </summary>
    internal void AddRoot(ISymbol symbol, Solution solution)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (symbol is null || solution is null)
        {
            SetError("There is no method, property, field, event or type at the caret.");
            return;
        }

        var identity = SymbolIdentity.Create(symbol, solution, solution.ProjectIds.FirstOrDefault());
        if (identity.IsEmpty)
        {
            SetError("That symbol cannot be tracked across edits, so it cannot root a graph.");
            return;
        }

        var root = ReferenceGraphNode.CreateRoot(
            ReferenceGraphDisplay.Of(symbol), symbol.Name, identity, SymbolGlyphs.For(symbol));

        _roots.Insert(0, root);
        SetError(null);
        StatusText.Text = $"Rooted at {root.DisplayText}.";
    }

    internal void SetErrorMessage(string message)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        SetError(message);
    }

    private void OnNodeExpanded(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!(e.OriginalSource is TreeViewItem item) || !(item.DataContext is ReferenceGraphNode node)) return;
        if (!node.IsExpandable || node.IsLoaded || node.IsLoading) return;

        BeginExpand(node);
    }

    /// <summary>
    /// Double-click navigates, and must not also open or close the row.
    /// <c>TreeViewItem.OnMouseLeftButtonDown</c> is what toggles <c>IsExpanded</c>, and it only does so
    /// when the event reaches it unhandled - so this hooks the tunnelling
    /// <c>PreviewMouseLeftButtonDown</c> on the TreeView, which runs before any item sees anything.
    /// Handling <c>MouseDoubleClick</c> was tried first and was too late in the chain.
    /// The <c>IsExpanded</c> restore afterwards is a belt-and-braces no-op when that works.
    /// </summary>
    private void OnTreePreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (e.ClickCount != 2 || _workspace is null) return;

        var source = e.OriginalSource as DependencyObject;

        // The expander chevron is a ToggleButton; double-clicking it means "expand", not "navigate".
        if (Ancestor<ToggleButton>(source) != null) return;

        if (!(Ancestor<TreeViewItem>(source)?.DataContext is ReferenceGraphNode node)) return;

        // A branch row has nowhere to navigate to, so leave the event alone and let it expand.
        if (node.DocumentId is null) return;

        e.Handled = true;

        var wasExpanded = node.IsExpanded;

        // Setting the same value is a no-op on the node, so this costs nothing when Handled did its job.
#pragma warning disable VSTHRD001, VSTHRD110
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => node.IsExpanded = wasExpanded));
#pragma warning restore VSTHRD001, VSTHRD110

        Navigate(node);
    }

    private static T Ancestor<T>(DependencyObject node) where T : DependencyObject
    {
        for (; node != null; node = VisualTreeHelper.GetParent(node))
            if (node is T match) return match;

        return null;
    }

    private void Navigate(ReferenceGraphNode node)
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
                .ResolveAsync(ranAgainst, _workspace.CurrentSolution, node.DocumentId, node.Span, CancellationToken.None)
                .ConfigureAwait(false);

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            SetError(DocumentNavigator.Navigate(ServiceProvider.GlobalProvider, target));
        }).FileAndForget("vs/roslynquery/referencegraph/navigate");
#pragma warning restore VSSDK007
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        RefreshExpanded();
    }

    private void OnScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!_ready) return;

        RefreshExpanded();
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!_ready) return;

        RefreshExpanded();
    }

    private void OnStopClick(object sender, RoutedEventArgs e) => _cancellation?.Cancel();

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        _roots.Clear();
        SetError(null);
        StatusText.Text = "Cleared.";
    }

    private void RefreshExpanded()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        foreach (var node in ReferenceGraphNode.ShallowestExpanded(_roots).ToList()) BeginExpand(node);
    }

    private void BeginExpand(ReferenceGraphNode node)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_workspace is null)
        {
            SetError("No Roslyn workspace is available. Open a solution and reopen this window.");
            return;
        }

        var solution = _workspace.CurrentSolution;
        var scope = CurrentScope;
        var filter = CurrentFilter;
        var token = SharedCancellation().Token;

        _ranAgainst = new WeakReference<Solution>(solution);
        node.IsLoading = true;
        _running++;
        StopButton.IsEnabled = true;

#pragma warning disable VSSDK007
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await ExpandCoreAsync(node, solution, scope, filter, token);
            }
            catch (OperationCanceledException)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                node.SetChildren([ReferenceGraphNode.CreateMessage("Cancelled.", node)]);
                node.IsLoaded = false;
                StatusText.Text = "Cancelled.";
            }
            catch (Exception ex)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                node.SetChildren([ReferenceGraphNode.CreateMessage("Failed.", node)]);
                node.IsLoaded = false;
                SetError(ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                node.IsLoading = false;
                if (--_running <= 0)
                {
                    _running = 0;
                    StopButton.IsEnabled = false;
                }
            }
        }).FileAndForget("vs/roslynquery/referencegraph/expand");
#pragma warning restore VSSDK007
    }

    private async Task ExpandCoreAsync(
        ReferenceGraphNode node, Solution solution, ScopeKind scope, ReferenceUsageKind filter, CancellationToken cancellationToken)
    {
        await TaskScheduler.Default;

        var symbol = await node.Identity.ResolveAsync(solution, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<ReferenceGraphNode> children;

        if (symbol is null)
        {
            children = [ReferenceGraphNode.CreateMessage("This symbol no longer exists in the current solution.", node)];
        }
        else if (node.Direction == ReferenceDirection.Incoming)
        {
            children = await ReferenceGraphEngine
                .FindIncomingAsync(symbol, solution, DocumentsFor(symbol, solution, scope), filter, node, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            children = await ReferenceGraphEngine
                .FindOutgoingAsync(symbol, solution, filter, node, cancellationToken)
                .ConfigureAwait(false);
        }

        if (children.Count == 0)
            children = [ReferenceGraphNode.CreateMessage("No references.", node)];

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        node.SetChildren(children);
    }

    /// <summary>
    /// The documents an incoming search may look at. Anchored on the symbol's own declaration rather
    /// than on the caret, so expanding a row means the same thing however long the window has been open.
    /// Null is the whole solution.
    /// </summary>
    private static IImmutableSet<Document> DocumentsFor(ISymbol symbol, Solution solution, ScopeKind scope)
    {
        if (scope == ScopeKind.Solution) return null;

        var tree = symbol.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree;
        var document = tree is null ? null : solution.GetDocument(tree);
        if (document is null) return null;

        if (scope == ScopeKind.Document) return ImmutableHashSet.Create(document);

        return document.Project.Documents.ToImmutableHashSet();
    }

    /// <summary>
    /// One token source for the whole window, per the same idiom as <c>QueryToolWindowControl</c>.
    /// Not disposed on replacement: expansions already holding the old token would see
    /// <see cref="ObjectDisposedException"/> instead of a clean cancellation.
    /// </summary>
    private CancellationTokenSource SharedCancellation()
    {
        if (_cancellation is null || _cancellation.IsCancellationRequested)
            _cancellation = new CancellationTokenSource();

        return _cancellation;
    }

    private void SetError(string message)
    {
        ErrorText.Text = message ?? string.Empty;
        ErrorText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }
}
