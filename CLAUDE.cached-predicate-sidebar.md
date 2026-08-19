# Cached predicate sidebar

<!-- RESUME PROTOCOL - any agent opening this file must follow this section before touching code. -->

## Resume protocol

You are resuming work described by this file. This file is the single source of truth for progress.

1. Read this entire file, then read every file listed in **Key files** and in the current step.
2. Run `git status` and `git log --oneline -5`. The working tree should be clean and `HEAD` should match the commit recorded in **Status**. If it does, this file is up to date; trust it and continue at the current step without re-auditing.
3. If the tree is dirty or `HEAD` does not match, reconcile first: figure out what happened, fix this file, commit the fix, then continue.
4. Work the first step that is not `[x]`. One step at a time.
5. **Every step ends with exactly one commit that contains both the code change and the update to this file.** They are never committed separately. This is what makes the file trustworthy.
6. Commit messages: one terse line, imperative, lowercase, no body, no trailing period.
7. `git commit` only. **Never** `git push`, `git commit --amend`, `git rebase`, or `git reset --hard` unless explicitly told to.
8. Never mark a step `[x]` before its **Verify** step has actually run (build) and, where noted, been manually exercised in the running VS experimental instance. If it fails, the step stays `[~]` and the failure goes in **Deviations**.
9. If reality diverges from the plan (a step is wrong, impossible, or unnecessary), amend the steps here and log it in **Deviations** in the same commit. Never silently deviate.
10. If a turn ends mid-step, the step stays `[~]` with a `Progress:` line describing exactly where it stopped and what is left. Commit whatever is coherent; if nothing is coherent, still update `Progress:` and commit only this file.
11. Do not ask for a plan-freshness check. Assume it is fresh unless step 2 says otherwise.
12. **Coordination note:** `PredicateCompiler.cs` and `Editor/PredicateCompletionSource.cs` had active unrelated work on this branch as of plan creation (predicate body-mode detection/completions, commits `9cda336`, `70e8d44`, `922cfb8`). Step 1 touches `PredicateCompiler.cs` only additively (one new public method, no changes to existing members). Re-read the file fresh before editing in case that other work has since moved lines around.

Step states: `[ ]` not started, `[~]` in progress, `[x]` done, `[!]` blocked, `[-]` dropped.

## Status

- **State:** done
- **Current step:** 5 - Add test coverage
- **Branch:** v0.2.0
- **Base commit:** 9cda336701ac463742b3f737385aeeb6450f1bee
- **Last synced commit:** (this commit)
- **Last updated:** 2026-08-19

## Goal

A collapsible, resizable sidebar docked to the right of `QueryToolWindowControl`'s existing content (not a separate tool window) lists every predicate currently sitting in `PredicateCompiler`'s cache, shown as its normalized (minified) text. Double-clicking an entry:

1. Restores that normalized text into the predicate input box.
2. Switches the Target combo to the `TargetKind` the entry was compiled for (SyntaxNode / SyntaxToken / IOperation) - this is what "target" means in this codebase, and it's the dimension coupled to the predicate's signature, so it must be restored.
3. Leaves the Scope combo exactly as the user currently has it set - scope is orthogonal to the predicate and the request is explicit that it must not change.
4. Runs, exactly as if Enter had been pressed.

The sidebar can collapse to zero width via a toggle button and can be resized by dragging a `GridSplitter` while expanded.

## Non-goals

- No restoration of caret/file context (`ScopeResolver.ActiveContext`) that a `ContainingMember`/`ContainingType` scope would have used originally. The user was explicit: scope stays as currently selected, full stop - if that scope needs an active document/caret and the current one doesn't provide one, that's the existing "Nothing in scope" behavior, unchanged.
- No persistence of sidebar width/collapsed state across VS sessions (no settings-store write). Runtime-only for v1.
- No changes to `PredicateCompiler`'s eviction policy, cache key shape, or `MaxCachedExpressions`.
- No de-duplication/grouping beyond what the cache itself already dedupes on (`(TargetKind, PredicateMode, normalizedText)`).
- No new tool window, docked panel, or pane - this lives inside the existing `QueryToolWindowControl` UserControl.

## Constraints and decisions

- **Data source is the compile cache itself, not a new run-history list:** `PredicateCompiler.Cache`'s key is already `(TargetKind, PredicateMode, string normalizedText)` - exactly the three pieces of data the sidebar needs to display an entry and restore+rerun it. Rejected: a separate `ObservableCollection` of run history populated from `Run()`, because it would duplicate what the cache already tracks and could drift from what's actually still cached (e.g. after LRU eviction).
- **"Target" = `TargetKind`, not caret/file context:** confirmed by `RoslynQuery/Query/QueryKinds.cs` and by how `Run()` reads `CurrentTarget`/`CurrentScope` independently. The user's phrasing ("target adjusted back to whatever the cached query ran against", "scope has nothing to do with the query") maps directly onto this codebase's existing Target vs Scope split. Rejected: treating "target" as the caret-derived `ActiveContext` `ScopeResolver` uses for `ContainingMember`/`ContainingType` scopes - that's scope machinery, not target, and restoring it would contradict "scope stays the same".
- **Refresh trigger: after each `Run()` completes, not event-driven:** `PredicateCompiler.Compile` is called from exactly one call site (`RunCoreAsync`), so polling the cache once per run is sufficient and avoids adding a static event to `PredicateCompiler` that every future caller would have to keep firing correctly. Rejected: `PredicateCompiler.Compiled`/`Evicted` events - more moving parts than one call site currently justifies; revisit if a second call site to `Compile` appears.
- **Snapshot ordering: most-recently-compiled first**, via `CacheOrder` (existing `ConcurrentQueue<(TargetKind, PredicateMode, string)>`), filtered to keys still present in `Cache` (handles eviction). `CacheOrder` only enqueues on a genuine cache miss (see `PredicateCompiler.cs:284` early-return on hit), so it is not a true LRU/MRU order, just insertion order - acceptable for a cache browser, not claimed to be "recently used".
- **Click target: double-click**, matching the existing `Results` ListBox's `OnResultDoubleClick` convention in the same window, so the two lists behave consistently. Single click only selects (standard ListBox behavior) - avoids an accidental rerun while scrolling/browsing.
- **Restore order on click: set Target combo first, then input Text, then call `Run()`.** `TargetCombo` is populated in `OnLoaded` in fixed order `SyntaxNode=0, SyntaxToken=1, Operation=2`, which is numerically identical to the `TargetKind` enum's declared values (`QueryKinds.cs:3-8`), so `TargetCombo.SelectedIndex = (int)item.Kind` is a direct, safe mapping - no lookup needed. Setting the index (when it changes) fires the existing `OnTargetChanged` handler, which sets `_input.Target` and calls `UpdateSignature()` - reuse that path rather than duplicating it.
- **No `PredicateMode` needs restoring explicitly.** `IPredicateInput` (`Editor/PredicateInput.cs:21-30`) has no `Mode` member. Mode is re-detected from text content by `PredicateCompiler.DetectMode` inside `Compile(TargetKind, string)` every time `Run()` calls `PredicateCompiler.Compile(target, expression)` (`QueryToolWindowControl.xaml.cs:241`). A normalized Body-mode string re-parses as incomplete-as-expression and is correctly redetected as Body. Confirmed no other place in `Editor/PredicateInput.cs` or the completion source stores mode as separate state that would need syncing.
- **Sidebar toggle button lives in the existing Row 1 button stack** (next to Run/Stop), not as a persistent always-visible strip inside the sidebar's own grid column. Simpler: one control to wire, no separate "collapsed rail" UI to design. Rejected: a slim always-visible strip at the far right when collapsed (typical VS Code sidebar pattern) - more layout work than this feature warrants; the toggle button in the toolbar row is discoverable enough.
- **Layout: 3-column outer `Grid`** (`* main | Auto splitter | fixed sidebar`), wrapping the current single root `Grid` unchanged as column 0's content. Rejected: `DockPanel` for the whole window - `GridSplitter` needs adjacent `ColumnDefinition`s to resize against, which is the standard WPF pattern and matches "resizable" directly.
- **`UserControl.MinWidth` bump from 420 to 560** (`QueryToolWindowControl.xaml:12`) so the window doesn't get crushed when the sidebar is open by default width (220) + splitter (4) + a usable main content minimum (~260, matching existing `MinWidth="420"` main content roughly). When the sidebar is collapsed the extra width is simply unused space, not a problem.

## Key files

- `RoslynQuery/Query/PredicateCompiler.cs` - add a `Snapshot()` method reading `Cache`/`CacheOrder` (around line 53, next to `CachedExpressionCount`). No other changes.
- `RoslynQuery/Query/QueryKinds.cs` - reference only, confirms `TargetKind`/`PredicateMode`/`ScopeKind` shapes. No changes.
- `RoslynQuery/ToolWindow/QueryToolWindowControl.xaml` - add outer 3-column `Grid`, `GridSplitter`, sidebar `Border`/`ListBox`, new `DataTemplate` (`CachedPredicateTemplate`), toggle `Button` in the Row 1 button stack. Reuse existing `ResultRow` style for `ItemContainerStyle`.
- `RoslynQuery/ToolWindow/QueryToolWindowControl.xaml.cs` - add `CachedPredicateItem` nested class, `_cachedPredicates` `ObservableCollection`, `RefreshCachedPredicates()`, `OnCachedPredicateDoubleClick`, `OnToggleSidebarClick`, one-line hook in `Run()`'s `finally` block.
- `RoslynQuery/ToolWindow/TargetMonikerConverter.cs` - reused as-is for the sidebar's per-row icon (binds to a `TargetKind`-valued property; property name doesn't matter to the converter). Read before Step 3 to confirm its `Convert` signature still only depends on value type, not source property name.
- `RoslynQuery/Editor/PredicateInput.cs` - reference only (`IPredicateInput.Text`/`Target` setters used to restore an entry). No changes.

## Steps

### 1. Expose a cache snapshot from PredicateCompiler `[x]`

- **Files:** `RoslynQuery/Query/PredicateCompiler.cs`
- **Do:** Add a public static method, placed near `CachedExpressionCount` (line 53):
  ```csharp
  public static IReadOnlyList<(TargetKind Kind, PredicateMode Mode, string Text)> Snapshot()
  {
      var order = CacheOrder.ToArray();
      var result = new List<(TargetKind, PredicateMode, string)>(order.Length);
      for (var i = order.Length - 1; i >= 0; i--)
      {
          var key = order[i];
          if (Cache.ContainsKey(key)) result.Add(key);
      }
      return result;
  }
  ```
  Add `using System.Collections.Generic;`-covered types if not already imported (file already has `System.Collections.Generic` at line 3 - confirm still true when editing). No changes to `Compile`, `Cache`, `CacheOrder`, or eviction logic.
- **Verify:** `dotnet build` on the solution succeeds with no new warnings from this file.
- **Commit:** `expose a cache snapshot from predicatecompiler`

### 2. Add the sidebar's data model and refresh wiring `[x]`

- **Files:** `RoslynQuery/ToolWindow/QueryToolWindowControl.xaml.cs`
- **Do:**
  - Add a nested `private sealed class CachedPredicateItem` next to `Choice<T>` (around line 25-37):
    ```csharp
    private sealed class CachedPredicateItem
    {
        public CachedPredicateItem(TargetKind kind, PredicateMode mode, string text)
        {
            Kind = kind;
            Mode = mode;
            Text = text;
        }

        public TargetKind Kind { get; }
        public PredicateMode Mode { get; }
        public string Text { get; }
        public string Preview => Text.Length > 300 ? Text.Substring(0, 300) + "..." : Text;
        public string Subtitle => Kind + " . " + Mode;
    }
    ```
    (Use a literal ASCII period as the separator per the no-em/en-dash rule; do not use "-" either since it could read as a minus in this context - a plain "." reads cleanly as "SyntaxNode . Expression".)
  - Add field: `private readonly ObservableCollection<CachedPredicateItem> _cachedPredicates = new ObservableCollection<CachedPredicateItem>();` next to `_hits` (line 39).
  - In the constructor (line 52-58), add `CachedPredicates.ItemsSource = _cachedPredicates;` next to `Results.ItemsSource = _hits;`. (This references the `CachedPredicates` `x:Name` added in Step 3 - if Step 3 hasn't landed yet, this step alone won't compile; do Steps 2 and 3 in the same commit, or reorder so Step 3's XAML lands first. Recommended: do XAML (Step 3) and this wiring (Step 2) as one combined step in practice, but keep them as separate plan entries for review granularity - see note in Step 3.)
  - Add method:
    ```csharp
    private void RefreshCachedPredicates()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _cachedPredicates.Clear();
        foreach (var (kind, mode, text) in PredicateCompiler.Snapshot())
            _cachedPredicates.Add(new CachedPredicateItem(kind, mode, text));
    }
    ```
  - Hook it into `Run()`'s `finally` block (line 217-224), after the existing `await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();` (line 219), before or after the button-state resets - order doesn't matter, add as its own line:
    ```csharp
    RefreshCachedPredicates();
    ```
- **Verify:** Will not build in isolation until Step 3's XAML names exist (`CachedPredicates` ListBox). Treat Steps 2+3 as landing together; verify with `dotnet build` after both are in place.
- **Commit:** `track cached predicates in an observable collection, refresh after each run`

### 3. Add the sidebar layout, splitter, and toggle to the XAML `[x]`

- **Files:** `RoslynQuery/ToolWindow/QueryToolWindowControl.xaml`
- **Do:**
  - Bump `MinWidth="420"` to `MinWidth="560"` on the root `UserControl` (line 12).
  - Add a new `DataTemplate x:Key="CachedPredicateTemplate"` in `UserControl.Resources` (after the existing `ResultTemplate`, before its closing `</UserControl.Resources>` at line 80), modeled on `ResultTemplate` (lines 58-79) but binding `Preview` (Consolas, `TextTrimming="CharacterEllipsis"`) as the primary line and `Subtitle` as the secondary line instead of `Kind`/`Location`, reusing the same `imaging:CrispImage`/`TargetMoniker` converter bound to `Kind`:
    ```xml
    <DataTemplate x:Key="CachedPredicateTemplate">
        <Grid>
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="Auto" />
                <ColumnDefinition Width="*" />
            </Grid.ColumnDefinitions>

            <imaging:CrispImage Grid.Column="0" Width="16" Height="16" Margin="0,1,8,0" VerticalAlignment="Top"
                                Moniker="{Binding Kind, Converter={StaticResource TargetMoniker}}" />

            <StackPanel Grid.Column="1">
                <TextBlock Text="{Binding Preview}" FontFamily="Consolas" TextTrimming="CharacterEllipsis" TextWrapping="Wrap" MaxHeight="48"
                           Foreground="{DynamicResource {x:Static vsshell:VsBrushes.ToolWindowTextKey}}" />
                <TextBlock Text="{Binding Subtitle}" Margin="0,1,0,0" TextTrimming="CharacterEllipsis"
                           Foreground="{DynamicResource {x:Static vsshell:VsBrushes.GrayTextKey}}" />
            </StackPanel>
        </Grid>
    </DataTemplate>
    ```
  - Wrap the existing root `<Grid Margin="8,8,8,0">...</Grid>` (lines 82-155) as column 0 of a new outer `Grid` with no margin of its own:
    ```xml
    <Grid>
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="*" MinWidth="260" />
            <ColumnDefinition x:Name="SidebarSplitterColumn" Width="Auto" />
            <ColumnDefinition x:Name="SidebarColumn" Width="220" MinWidth="140" />
        </Grid.ColumnDefinitions>

        <Grid Grid.Column="0" Margin="8,8,8,0">
            <!-- existing content, lines 83-154, UNCHANGED except: add the toggle Button
                 to the Row 1 StackPanel (see below) -->
        </Grid>

        <GridSplitter x:Name="SidebarSplitter" Grid.Column="1" Width="4" HorizontalAlignment="Stretch"
                      VerticalAlignment="Stretch" ResizeBehavior="PreviousAndNext"
                      Background="{DynamicResource {x:Static vsshell:VsBrushes.CommandBarGradientKey}}" />

        <Border x:Name="SidebarPane" Grid.Column="2" Margin="4,8,0,0" BorderThickness="1,0,0,0"
                BorderBrush="{DynamicResource {x:Static vsshell:VsBrushes.CommandBarBorderKey}}">
            <Grid>
                <Grid.RowDefinitions>
                    <RowDefinition Height="Auto" />
                    <RowDefinition Height="*" />
                </Grid.RowDefinitions>

                <TextBlock Grid.Row="0" Text="cached predicates" Style="{StaticResource Caption}" Margin="6,0,0,4" />

                <ListBox x:Name="CachedPredicates" Grid.Row="1"
                         BorderThickness="0" Background="Transparent"
                         HorizontalContentAlignment="Stretch"
                         ScrollViewer.HorizontalScrollBarVisibility="Disabled"
                         VirtualizingStackPanel.IsVirtualizing="True"
                         VirtualizingStackPanel.VirtualizationMode="Recycling"
                         ItemContainerStyle="{StaticResource ResultRow}"
                         ItemTemplate="{StaticResource CachedPredicateTemplate}"
                         MouseDoubleClick="OnCachedPredicateDoubleClick" />
            </Grid>
        </Border>
    </Grid>
    ```
    Note the `VsBrushes.CommandBarGradientKey`/`CommandBarBorderKey` keys are a guess at visually-reasonable existing theme brushes for a splitter/divider - grep `VsResourceKeys`/`VsBrushes` usage elsewhere in VS SDK samples if these don't resolve at compile time (XAML `DynamicResource` failures are silent at compile time but visible at runtime); fall back to `VsBrushes.ToolWindowBorderKey`/`ScrollBarBackgroundKey` if so, or plain `#FF3F3F46`-style literal only as a last resort.
  - Add the toggle button to the existing Row 1 `StackPanel Grid.Column="1"` (lines 123-130), alongside `RunButton`/`StopButton`:
    ```xml
    <Button x:Name="SidebarToggleButton" Content="cache" MinWidth="48" Margin="6,0,0,0" Padding="8,2"
            Style="{DynamicResource {x:Static vsshell:VsResourceKeys.ThemedDialogButtonStyleKey}}"
            Click="OnToggleSidebarClick" ToolTip="Show or hide the cached predicates sidebar" />
    ```
- **Verify:** `dotnet build` succeeds (XAML compiles, all `x:Name` references resolve). Manually launch the VS experimental instance (existing project launch profile), open the RoslynQuery tool window, confirm: sidebar renders at 220px, splitter drags to resize both ways down to each side's `MinWidth`, toggle button collapses/expands it (once Step 4 wires the click handler - until then the button will exist but do nothing; acceptable mid-plan state, do not mark this step `[x]` until Step 4's toggle behavior is also verified working end-to-end, since a dead button is not a finished sidebar).
- **Commit:** `add resizable sidebar layout and cached-predicates list to the tool window`

### 4. Wire double-click restore+rerun and the collapse/expand toggle `[x]`

- **Files:** `RoslynQuery/ToolWindow/QueryToolWindowControl.xaml.cs`
- **Do:**
  - Add field: `private double _sidebarWidth = 220;` next to `_ranAgainst` (line 50).
  - Add handler, modeled on `OnResultDoubleClick` (lines 141-163) but synchronous (no navigation/async needed):
    ```csharp
    private void OnCachedPredicateDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!(CachedPredicates.SelectedItem is CachedPredicateItem item)) return;

        TargetCombo.SelectedIndex = (int)item.Kind;
        _input.Text = item.Text;
        Run();
    }
    ```
  - Add toggle handler:
    ```csharp
    private void OnToggleSidebarClick(object sender, RoutedEventArgs e)
    {
        var collapsed = SidebarColumn.Width.Value == 0;
        if (collapsed)
        {
            SidebarColumn.Width = new GridLength(_sidebarWidth);
            SidebarSplitterColumn.Width = GridLength.Auto;
            SidebarSplitter.Visibility = Visibility.Visible;
            SidebarPane.Visibility = Visibility.Visible;
        }
        else
        {
            _sidebarWidth = SidebarColumn.Width.Value > 0 ? SidebarColumn.Width.Value : _sidebarWidth;
            SidebarColumn.Width = new GridLength(0);
            SidebarSplitterColumn.Width = new GridLength(0);
            SidebarSplitter.Visibility = Visibility.Collapsed;
            SidebarPane.Visibility = Visibility.Collapsed;
        }
    }
    ```
  - Call `RefreshCachedPredicates();` once at the end of `OnLoaded` (after line 121's focus dispatch, or anywhere after `_workspace`/UI is set up) so the sidebar isn't empty on first open if the process-wide `PredicateCompiler.Cache` already has entries from an earlier run in this VS session (the cache is a static, process-lifetime cache - a second tool window instance, or a reopened one, sees prior entries immediately, before any `Run()` in *this* instance has happened).
- **Verify:** In the VS experimental instance: run two or three different predicates (mix of Expression and Body mode, mix of Target kinds), confirm each appears in the sidebar after running, most-recent first. Double-click an older entry with a different Target than currently selected: confirm Target combo updates, input box shows the minified text, Scope combo is untouched, and results run against the current Scope. Toggle the sidebar closed and back open, confirm width is restored to what it was before collapsing (not reset to 220 unless it was never resized).
- **Commit:** `restore and rerun cached predicates from the sidebar, wire collapse toggle`

### 5. Add test coverage `[x]`

- **Files:** `RoslynQuery.Tests/PredicateCompilerSnapshotTests.cs` (new), `RoslynQuery.Tests/CachedPredicateItemTests.cs` (new)
- **Do:** Cover the two pure, testable surfaces this feature added:
  - `PredicateCompiler.Snapshot()`: contains a freshly compiled entry, most-recent-first ordering, a cache-hit recompile doesn't duplicate the entry, a Body-mode entry reports `PredicateMode.Body`, and distinct `TargetKind`s for the same text both appear. Every case compiles a `Guid`-derived unique token and matches on `e.Text.Contains(token.ToString())` rather than hand-computing `Normalize`'s expected output string - reproducing the normalizer's own spacing rules in the test would just duplicate (and risk diverging from) the logic under test, same rationale `PredicateCompilerCachingTests` already documents for itself.
  - `CachedPredicateItem`: `Preview` unchanged under/at the 300-char limit, truncated with `"..."` over it; `Subtitle` is `"{Kind} . {Mode}"`; constructor exposes `Kind`/`Mode`/`Text` unchanged. `[Theory]` cases pass `TargetKind`/`PredicateMode` as `int` (CS0051: an internal enum can't be a typed argument on a public `[Theory]` method), matching the existing workaround in `PredicateTemplateBodyModeTests.AllKinds`.
  - No test attempts the WPF code-behind interaction handlers (`OnCachedPredicateDoubleClick`, `OnToggleSidebarClick`, `RefreshCachedPredicates`) - consistent with the rest of this codebase, which has zero tests over `QueryToolWindowControl.xaml.cs` because it depends on `ThreadHelper.ThrowIfNotOnUIThread()`/live VS shell services that aren't available outside a running IDE.
- **Verify:** `dotnet test RoslynQuery.Tests/RoslynQuery.Tests.csproj` reported a handshake failure in this sandbox unrelated to the change (`Zero tests ran`, exit code 5) - ran the built `RoslynQuery.Tests/bin/Debug/net472/RoslynQuery.Tests.exe` directly instead: `174 Total, 0 Errors, 0 Failed` (161 pre-existing + 13 new, see Deviations - two extra `Snapshot()` cases were added to `PredicateCompilerSnapshotTests.cs` by a concurrent process after this step's initial 11).
- **Commit:** `add tests for the predicate cache snapshot and sidebar item formatting`

## Deviations

- 2026-08-19 - Steps 2, 3, and 4 were implemented and committed together instead of as three separate commits. Reason found while executing Step 2: the XAML added in Step 3 wires `Click="OnToggleSidebarClick"` and `MouseDoubleClick="OnCachedPredicateDoubleClick"` directly to code-behind methods that Step 4 adds - XAML-to-code-behind event wiring requires the handler to exist for the partial class to compile at all, so Steps 3 and 4 (and 2, which Step 3's `CachedPredicates` `x:Name` is needed for) were never independently compilable. The plan's own Step 2 note already flagged this tension without fully resolving it. All three steps' code changes landed in one commit; each step is still checked off individually above since the plan's original per-step breakdown remains an accurate description of the work, just not of the commit boundaries.
- 2026-08-19 - Per user instruction mid-implementation: build verification was relaxed from "after every step" to "once the feature is coherent," and test coverage was added as a new Step 5 not in the original plan.
- 2026-08-19 - `RoslynQuery.Tests/PredicateCompilerSnapshotTests.cs` gained two additional test cases (`Snapshot_DistinctModesForTheSameText_AreSeparateEntries`, `Snapshot_SkipsKeysEvictedSinceBeingEnqueued`) from a concurrent process after this session wrote the file's original 5 cases - not authored in this session. Reviewed both: correct, exercise real gaps in the original 5 (mode as part of the cache key; the `Cache.ContainsKey` eviction-filter branch in `Snapshot()`), written in this codebase's existing style. Kept as-is per instruction to treat externally-changed files as deliberate rather than reverting them.
- 2026-08-19 - Bug found via user screenshot after manual verification: collapsing the sidebar left a permanent ~140px gap of dead space on the right where the main content should have expanded to fill it. Cause: `SidebarColumn`'s `MinWidth="140"` (set in XAML, meant to bound how far the `GridSplitter` can be dragged while expanded) is a hard floor on the column's rendered width that `Width` cannot override - `OnToggleSidebarClick`'s collapse branch set `Width = new GridLength(0)` but never touched `MinWidth`, so the column stayed pinned open at 140px regardless. Fixed in `QueryToolWindowControl.xaml.cs` (`OnToggleSidebarClick`): toggle `SidebarColumn.MinWidth` between `0` (collapsed) and `140` (expanded) alongside `Width`. Restore+rerun itself was confirmed working correctly by the user before this fix.

- 2026-08-19 - HEAD had moved to `531ac19` by the time Step 1 executed (two commits landed after this plan's base: `27da1c4` "Key the predicate cache on normalized text, compile it as typed", `531ac19` "Rewrite directive error message"). Checked the diff: only `NormalizeBody` changed (now collapses all gaps, line breaks included, to a single space instead of preserving line breaks) and the directive error message text changed. Neither touches `Cache`/`CacheOrder`'s key shape or `Compile`'s call sites, so Step 1's `Snapshot()` addition was unaffected and applied as originally planned. Folded this reconciliation into Step 1's commit rather than a separate no-op commit.

## Open questions

- **Splitter/divider brush keys** (`VsBrushes.CommandBarGradientKey`, `VsBrushes.CommandBarBorderKey`) used in Step 3 are a best guess for a visually subtle divider consistent with the rest of the window's `VsBrushes` usage - they were not verified against the installed VS SDK version's actual resource dictionary (no existing `GridSplitter`/divider precedent anywhere in this codebase to copy from, per the initial research pass). Whoever executes Step 3 should verify these resolve at design-time/runtime and swap to a confirmed-good key if not.
- **Preview truncation length (300 chars) and max height (48px, ~3 lines)** in `CachedPredicateItem.Preview`/the template are arbitrary starting values, not requested by the user. Adjust to taste during Step 3's manual verification pass - not worth a separate step.
