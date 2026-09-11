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

    private readonly ObservableCollection<ReferenceGraphNode> _roots = [];

    private VisualStudioWorkspace _workspace;
    private CancellationTokenSource _cancellation;
    private int _running;
    private bool _initialized;

    // Separate from _initialized: the scope combo raises SelectionChanged while OnLoaded is still
    // populating it, and that must not count as the user changing anything.
    private bool _ready;

    // Weak: a Solution roots its compilations, and the tree can sit on screen for hours.
    private WeakReference<Solution> _ranAgainst;

    public ReferenceGraphToolWindowControl()
    {
        InitializeComponent();

        Tree.ItemsSource = _roots;
        Loaded += OnLoaded;
    }

    private ScopeKind CurrentScope => ((Choice<ScopeKind>)ScopeCombo.SelectedItem)?.Value ?? ScopeKind.Project;

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

        ClassificationBrushes.Initialize(componentModel);
        ClassificationBrushes.Changed += OnClassificationsChanged;
        SignatureText.BrushResolver = ClassificationBrushes.For;

        if (_workspace is null) SetError("No Roslyn workspace is available. Open a solution and reopen this window.");
        else StatusText.Text = "Right-click a member or type in the editor and choose View Reference Graph.";

        _ready = true;
    }

    private void OnClassificationsChanged(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        Tree.Items.Refresh();
    }

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
            ReferenceGraphDisplay.Of(symbol), identity, SymbolGlyphs.For(symbol), ReferenceAnalyzers.For(symbol),
            ReferenceGraphDisplay.SignatureOf(symbol));

        _roots.Insert(0, root);
        SetError(null);
        StatusText.Text = $"Added root '{root.DisplayText}'.";
    }

    internal void SetErrorMessage(string message)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        SetError(message);
    }

    private void OnNodeExpanded(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (e.OriginalSource is not TreeViewItem item || item.DataContext is not ReferenceGraphNode node) return;
        if (!node.IsExpandable || node.IsLoaded || node.IsLoading) return;

        BeginExpand(node);
    }

    /// <summary>
    /// Double-click navigates without toggling the row. Handling <c>MouseDoubleClick</c> was tried
    /// first and was too late to stop <c>TreeViewItem.OnMouseLeftButtonDown</c>'s own toggle; this
    /// hooks the tunnelling <c>PreviewMouseLeftButtonDown</c> instead, which runs first.
    /// </summary>
    private void OnTreePreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (e.ClickCount != 2 || _workspace is null) return;

        var source = e.OriginalSource as DependencyObject;

        // The expander chevron is a ToggleButton; double-clicking it means "expand", not "navigate".
        if (Ancestor<ToggleButton>(source) != null) return;

        if (Ancestor<TreeViewItem>(source)?.DataContext is not ReferenceGraphNode node) return;

        // A branch row has nowhere to navigate to, so leave the event alone and let it expand.
        if (node.DocumentId is null && !node.IsFromMetadata) return;

        e.Handled = true;

        var wasExpanded = node.IsExpanded;

        // Setting the same value is a no-op on the node, so this costs nothing when Handled did its job.
#pragma warning disable VSTHRD001, VSTHRD110
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => node.IsExpanded = wasExpanded));
#pragma warning restore VSTHRD001, VSTHRD110

        if (node.IsFromMetadata) NavigateToDecompiled(node);
        else Navigate(node);
    }

    private void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (e.Key == Key.Delete)
        {
            if (Tree.SelectedItem is ReferenceGraphNode root && root.Parent is null && _roots.Remove(root))
            {
                e.Handled = true;
                StatusText.Text = $"Dropped root '{root.DisplayText}'.";
            }
            return;
        }

        if (e.Key != Key.Enter || _workspace is null) return;
        if (Tree.SelectedItem is not ReferenceGraphNode node || (node.DocumentId is null && !node.IsFromMetadata)) return;

        e.Handled = true;

        if (node.IsFromMetadata) NavigateToDecompiled(node);
        else Navigate(node);
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

    private void NavigateToDecompiled(ReferenceGraphNode node)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var identity = node.Identity;
        var solution = _workspace.CurrentSolution;
        var name = node.DisplayText;

        SetError(null);
        StatusText.Text = $"Decompiling {name}...";

#pragma warning disable VSSDK007
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await TaskScheduler.Default;

            NavigationTarget target = null;
            string failure = null;

            try
            {
                var assembly = await MetadataAssemblyLocator.PathOfAsync(identity, solution, CancellationToken.None).ConfigureAwait(false);

                if (assembly is null)
                {
                    failure = $"No assembly file backs {name}, so there is nothing to decompile.";
                }
                else
                {
                    var source = DecompiledSourceProvider.Decompile(assembly, identity.DeclarationId);

                    if (!source.Succeeded)
                    {
                        failure = source.Failure;
                    }
                    else
                    {
                        var path = DecompiledSourceFiles.Write(
                            DecompiledSourceFiles.DefaultRoot, source.AssemblyName, source.AssemblyVersion, source.TypeFullName, source.Text);

                        target = new NavigationTarget
                        {
                            FilePath = path,
                            Line = source.Line,
                            Column = source.Column,
                            EndLine = source.Line,
                            EndColumn = source.Column
                        };
                    }
                }
            }
            catch (Exception ex) when (IsDecompilerUnavailable(ex))
            {
                failure = "The decompiler Visual Studio ships could not be loaded, so metadata rows cannot be opened: " + ex.Message;
            }
            catch (Exception ex)
            {
                failure = $"Decompiling {name} failed: {ex.GetType().Name}: {ex.Message}";
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            SetError(failure ?? DocumentNavigator.Navigate(ServiceProvider.GlobalProvider, target));
            StatusText.Text = failure is null ? $"Opened decompiled {name}." : string.Empty;
        }).FileAndForget("vs/roslynquery/referencegraph/decompile");
#pragma warning restore VSSDK007
    }

    private static bool IsDecompilerUnavailable(Exception exception)
    {
        var missing = (exception as System.IO.FileNotFoundException)?.FileName ?? (exception as System.IO.FileLoadException)?.FileName;

        if (missing != null)
        {
            return missing.StartsWith("ICSharpCode.Decompiler", StringComparison.OrdinalIgnoreCase)
                || missing.StartsWith("System.Reflection.Metadata", StringComparison.OrdinalIgnoreCase)
                || missing.StartsWith("System.Collections.Immutable", StringComparison.OrdinalIgnoreCase)
                || missing.StartsWith("System.Memory", StringComparison.OrdinalIgnoreCase);
        }

        return exception is TypeLoadException || exception is MissingMethodException || exception is TypeInitializationException;
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

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        _cancellation?.Cancel();

        foreach (var node in ReferenceGraphNode.ShallowestLoaded(_roots).ToList()) node.ResetToUnloaded();
        StatusText.Text = "Stopped. Expanded rows were cleared; expand them again to re-read.";
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var count = _roots.Count;
        _roots.Clear();
        SetError(null);
        StatusText.Text = $"Dropped {count} root{(count == 1 ? "" : "s")}.";
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
                await ExpandCoreAsync(node, solution, scope, token);
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
        ReferenceGraphNode node, Solution solution, ScopeKind scope, CancellationToken cancellationToken)
    {
        if (node.Analyzer is null) return;

        await TaskScheduler.Default;

        var symbol = await node.Identity.ResolveAsync(solution, cancellationToken).ConfigureAwait(false);

        if (symbol is null)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            node.SetChildren([ReferenceGraphNode.CreateMessage("This symbol no longer exists in the current solution.", node)]);
            return;
        }

        var result = await ReferenceGraphEngine
            .RunAsync(node.Analyzer.Value, symbol, solution, DocumentsFor(symbol, solution, scope), node, cancellationToken)
            .ConfigureAwait(false);

        // An empty branch gets no children at all, which is what drops its expander.
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        node.ApplyResults(result);
    }

    private static IImmutableSet<Document> DocumentsFor(ISymbol symbol, Solution solution, ScopeKind scope)
    {
        if (scope == ScopeKind.Solution) return null;

        var tree = symbol.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree;
        var document = tree is null ? null : solution.GetDocument(tree);
        if (document is null) return null;

        if (scope == ScopeKind.Document) return ImmutableHashSet.Create(document);

        return document.Project.Documents.ToImmutableHashSet();
    }

    /// <summary>Not disposed on replacement, or expansions already holding the old token would see <see cref="ObjectDisposedException"/> instead of a clean cancellation.</summary>
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
