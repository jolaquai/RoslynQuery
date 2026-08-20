# Reference Graph tool window

<!-- RESUME PROTOCOL - any agent opening this file must follow this section before touching code. -->

## Resume protocol

You are resuming work described by this file. This file is the single source of truth for progress.

1. Read this entire file, then read every file listed in **Key files** and in the current step.
2. Run `git status` and `git log --oneline -5`. The working tree should be clean and `HEAD` should match the commit recorded in **Status**. If it does, this file is up to date; trust it and continue at the current step without re-auditing.
3. If the tree is dirty or `HEAD` does not match, reconcile first: figure out what happened, fix this file, commit the fix, then continue.
4. Work the first step that is not `[x]`. One step at a time.
5. **Every step ends with exactly one commit that contains both the code change and the update to this file.** They are never committed separately. This is what makes the file trustworthy.
6. Commit messages: one terse line, imperative, lowercase, no body, no trailing period. Example: `add reference usage classifier`.
7. `git commit` only. **Never** `git push`, `git commit --amend`, `git rebase`, or `git reset --hard` unless explicitly told to.
8. Never mark a step `[x]` before its **Verify** command has actually run and passed. If it fails, the step stays `[~]` and the failure goes in **Deviations**.
9. If reality diverges from the plan (a step is wrong, impossible, or unnecessary), amend the steps here and log it in **Deviations** in the same commit. Never silently deviate.
10. If a turn ends mid-step, the step stays `[~]` with a `Progress:` line describing exactly where it stopped and what is left. Commit whatever is coherent; if nothing is coherent, still update `Progress:` and commit only this file.
11. Do not ask for a plan-freshness check. Assume it is fresh unless step 2 says otherwise.

Step states: `[ ]` not started, `[~]` in progress, `[x]` done, `[!]` blocked, `[-]` dropped.

## Status

- **State:** in-progress - all code complete, blocked only on step 8's manual smoke test
- **Current step:** 8 and 12 - manual F5 smoke test (five defects found and fixed so far; needs re-running)
- **Branch:** v0.3.0
- **Base commit:** e1c9fd34b4185a1f071a2fc0c9da3e0f51643a15
- **Last synced commit subject:** `stop double-click from toggling row expansion` (verify with `git log -1 --format=%s`)
- **Last updated:** 2026-08-20

## Goal

A second VSIX tool window, "Reference Graph", alongside the existing "Roslyn Query" window. Right-clicking a method, constructor, property, field, event, or type in the editor (or opening the window from View > Other Windows) roots a lazily-expandable tree with two branches: "References To 'X'" (who references X) and "References From 'X'" (what X references). Each further node expands the same way, recursively, in whichever direction its branch started in. Styled like VS's built-in Call Hierarchy window but generalized to all reference kinds, not just calls. Done = builds clean, engine unit tests pass, and a manual F5 smoke test in the experimental instance shows both branches populating, recursion terminating cleanly, the usage-kind filter flyout live-refreshing the tree, and double-click navigation landing on the right line.

## Non-goals

- No per-call-site leaf level under a node (a node's secondary line shows a count + kind breakdown instead; double-click jumps to the first location).
- No cross-solution / metadata-source callee resolution (Roslyn's `SymbolFinder` already can't do this; not attempted).
- No local functions or lambdas as valid graph roots.
- No automated UI tests for the WPF tool window itself - verified by manual smoke test only, matching how `QueryToolWindowControl` has none today.
- Do not retarget the VSIX project off net472. That TFM is a VSSDK/VS2022 extensibility constraint, not a stack choice - the user's usual "always latest/preview" preference does not apply to this project.

## Constraints and decisions

- **Root kinds:** methods, constructors, properties, fields, events, and types are all valid roots. Rejected: methods-only (matches VS's built-in Call Hierarchy but is narrower than the user's explicit ask - "references the current member" plus a follow-up answer explicitly adding types).
- **Reference-kind filter is unified across both directions:** one `[Flags] ReferenceUsageKind { Invocation, Read, Write, Construction, TypeReference }` enum and one `ReferenceUsageClassifier` used by both the incoming and outgoing engine paths, exposed in the UI as a live checkbox flyout (not a fixed include/exclude choice). Default enabled = `Invocation | Read | Write | Construction`; `TypeReference` starts off. Rejected: hardcoding one fixed scope for outgoing (user explicitly asked for a toggleable checkbox filter instead of picking one of the two original proposals).
- **Node granularity:** one tree node per referencing/referenced symbol, carrying a list of individual locations (each tagged with its `ReferenceUsageKind`) for the secondary "N refs (read/write breakdown)" line; double-click navigates to the first location. Rejected: a separate leaf node per call site (adds tree depth not justified for v1; can be added later without changing the engine).
- **Symbol identity across expansions:** `ReferenceGraphNode` stores a `SymbolIdentity` (declaring `ProjectId` + the symbol's documentation-comment declaration id), not a live `ISymbol` - re-resolved against `_workspace.CurrentSolution` only when a node is expanded. Rejected: holding the `ISymbol` directly, which would pin the compilation that produced it alive for as long as the tool window stays open (the same problem `QueryHit` already deliberately avoids - see `RoslynQuery/Query/QueryHit.cs:10-13`). Also rejected: `Microsoft.CodeAnalysis.SymbolKey`, which the step-3 probe showed is **internal** to Microsoft.CodeAnalysis.Workspaces (see **Deviations**).
- **Multi-root history:** every invocation (context menu or the window's own toolbar) prepends a new root node to the tree instead of replacing the current one - mirrors VS's real Call Hierarchy behavior. The trash-icon button clears the whole root list. Rejected: single-root replace-on-invoke (loses history; the screenshot's trash icon implies a list worth clearing).
- **Scope combo (Current Document / Current Project / My Solution) only affects the "References To" (incoming) branch.** "References From" (outgoing) is inherently local to the root's own declaration and never searches outside it - document this as a tooltip on the combo, not as a separate disabled state.
- **Cancellation** uses one shared `CancellationTokenSource` field, same idiom as `QueryToolWindowControl._cancellation` (`RoslynQuery/ToolWindow/QueryToolWindowControl.xaml.cs:45`) - not per-node-expansion cancellation tokens. Simplicity over a rare concurrent-expansion benefit.

## Key files

- `RoslynQuery/Query/QueryEngine.cs` - tree-walking pattern to copy for `FindOutgoingAsync`'s body walk (`ScanNodesAsync`, `RoslynQuery/Query/QueryEngine.cs:180`).
- `RoslynQuery/Query/ScopeResolver.cs` - `GetActiveContext` (caret file/line/column, reuse as-is, line 49), `ResolveDeclarationAsync`'s enclosing-symbol walk to copy for incoming grouping (line 131), `IsDeclarationSymbol` (line 154) as the model for which symbol kinds count as declarations.
- `RoslynQuery/Query/QueryHit.cs` - "hold no live Roslyn object" discipline to follow for `ReferenceGraphNode` (lines 10-13).
- `RoslynQuery/ToolWindow/QueryToolWindowControl.xaml` / `.xaml.cs` - WPF styling conventions, toolbar layout, threading pattern (`Run`/`RunCoreAsync`, lines 231-347), navigation on double-click (`OnResultDoubleClick`, lines 148-170), error reporting (`SetError`, lines 225-229) - the template for the new control.
- `RoslynQuery/ToolWindow/QueryToolWindow.cs`, `RoslynQuery/ToolWindow/TargetMonikerConverter.cs` - templates for `ReferenceGraphToolWindow.cs` and `SymbolGlyphMonikerConverter.cs`.
- `RoslynQuery/Navigation/DocumentNavigator.cs`, `RoslynQuery/Navigation/SpanMapper.cs` - reused unchanged for navigation.
- `RoslynQuery/RoslynQueryPackage.cs`, `RoslynQuery/RoslynQueryPackage.vsct` - existing command/tool-window registration to extend.
- `RoslynQuery.Tests/PredicateAwaitTests.cs:21-41` - `AdhocWorkspace` + `ProjectInfo.Create` + `workspace.AddDocument` fixture pattern to copy for every new test file.
- `RoslynQuery/RoslynQuery.csproj` - already has `InternalsVisibleTo` for the test project; no new wiring needed for `internal` types.
- `RoslynQuery.slnx` - solution file (not `.sln`).

**Folder convention:** every file this plan adds lives in a subfolder, never at a project root -
production code under `RoslynQuery/ReferenceGraph/` and `RoslynQuery/ToolWindow/`, tests under
`RoslynQuery.Tests/ReferenceGraph/`, `RoslynQuery.Tests/ToolWindow/`, and shared test fixtures under
`RoslynQuery.Tests/Infrastructure/`. Namespaces stay flat (`RoslynQuery.Tests`) to match the existing
test project and to keep this plan's `-class` filters valid.

## Steps

### 1. Reference-kind classification model `[x]`

- **Files:** `RoslynQuery/ReferenceGraph/ReferenceUsageKind.cs` (new), `RoslynQuery/ReferenceGraph/ReferenceUsageClassifier.cs` (new), `RoslynQuery.Tests/ReferenceUsageClassifierTests.cs` (new)
- **Do:** Add `[Flags] internal enum ReferenceUsageKind { Invocation = 1, Read = 2, Write = 4, Construction = 8, TypeReference = 16 }`. Add `internal static class ReferenceUsageClassifier` with `Classify(SyntaxNode occurrence, ISymbol target) -> ReferenceUsageKind`: inspect `occurrence`'s ancestor syntax - callee of `InvocationExpressionSyntax` -> `Invocation`; LHS of `AssignmentExpressionSyntax`, `ref`/`out` argument, or operand of `++`/`--` -> `Write`; inside `ObjectCreationExpressionSyntax`/`ConstructorInitializerSyntax` -> `Construction`; inside `TypeSyntax`/`BaseListSyntax`/`CastExpressionSyntax`/`TypeOfExpressionSyntax`/`CatchClauseSyntax`/type-argument list -> `TypeReference`; otherwise `Read`. A single occurrence can return combined flags only where genuinely ambiguous (e.g. compound assignment `x += 1` on a field is both `Read` and `Write`) - default to the single most specific flag elsewhere.
- **Verify:** `dotnet build RoslynQuery.slnx -c Debug` succeeds, then `RoslynQuery.Tests/bin/Debug/net472/RoslynQuery.Tests.exe -class "RoslynQuery.Tests.ReferenceUsageClassifierTests"` passes. Cover: plain invocation, field read, field write (assignment LHS), compound assignment (Read|Write), `new Foo()` construction, `this()`/`base()` initializer, parameter type reference, cast, `typeof`, generic type argument, catch clause type.
- **Commit:** `add reference usage kind and classifier`

### 2. Caret symbol resolution `[x]`

- **Files:** `RoslynQuery/ReferenceGraph/SymbolResolver.cs` (new), `RoslynQuery.Tests/SymbolResolverTests.cs` (new)
- **Do:** `internal static class SymbolResolver` with `ResolveAtCaretAsync(Solution solution, ActiveContext active, CancellationToken)`. Find the document the same way `ScopeResolver`'s private `FindDocument` does (`RoslynQuery/Query/ScopeResolver.cs:171`) - either call it if accessible or duplicate the two-line lookup (it's `private static`, so duplicate rather than change its visibility). Get the semantic model and syntax root, find the token at the caret position (reuse `ScopeResolver`'s `ToPosition` logic at line 179, likely duplicated for the same visibility reason), try `GetDeclaredSymbol` on the token's parent first (caret on a declaration), fall back to `GetSymbolInfo` (caret on a usage), fall back further by walking `token.Parent.Parent` a few levels if the immediate node binds to nothing useful. Restrict the accepted result to `SymbolKind` in {Method, Property, Field, Event, NamedType} (constructors are `IMethodSymbol` with `MethodKind.Constructor`, already covered by `Method`).
- **Verify:** `dotnet build RoslynQuery.slnx -c Debug` succeeds, then `RoslynQuery.Tests/bin/Debug/net472/RoslynQuery.Tests.exe -class "RoslynQuery.Tests.SymbolResolverTests"` passes. Cover: caret on a method declaration's name, caret on a call site, caret on a field declaration, caret inside a method body with the caret actually over an unrelated local (should resolve to the containing method via the declared-symbol path only if caret truly lands on a declaration token - otherwise confirm it resolves to whatever symbol the token under the caret actually binds to, not a silent fallback to "enclosing method").
- **Commit:** `add caret symbol resolver`

### 3. Reference graph node model `[x]`

- **Files:** `RoslynQuery/ReferenceGraph/ReferenceGraphNode.cs` (new), `RoslynQuery/ReferenceGraph/ReferenceDirection.cs` (new, `internal enum ReferenceDirection { Incoming, Outgoing }`), `RoslynQuery.Tests/ReferenceGraph/ReferenceGraphNodeTests.cs` (new)
- **Do:** Before writing this file, empirically verify `Microsoft.CodeAnalysis.SymbolKey`'s exact API (static `Create`, instance `Resolve`, `GetSymbolKey` extension availability) against the Roslyn package version this project references, via a throwaway console probe - do not commit the probe. Then add `internal sealed class ReferenceGraphNode : INotifyPropertyChanged` with: `DisplayText`, `SecondaryText`, `SymbolKindForGlyph` (or similar, feeds `SymbolGlyphMonikerConverter` later), `DocumentId` + primary `TextSpan` (first location), `IReadOnlyList<(DocumentId DocumentId, TextSpan Span, ReferenceUsageKind Kind)> Locations`, `ReferenceDirection Direction`, `ReferenceGraphNode Parent` (for ancestor-chain cycle checks in step 5), `bool IsRecursive`, a stored `SymbolKey` string/struct for re-resolution, and a lazily-populated `ObservableCollection<ReferenceGraphNode> Children` seeded with a single placeholder node so the tree shows an expand arrow before the real fetch. Add a helper `bool HasAncestor(SymbolKey key)` walking `Parent` up.
- **Verify:** `dotnet build RoslynQuery.slnx -c Debug` succeeds, then `RoslynQuery.Tests/bin/Debug/net472/RoslynQuery.Tests.exe -class "RoslynQuery.Tests.ReferenceGraphNodeTests"` passes. Cover: constructing a node and resolving its stored `SymbolKey` back to the original `ISymbol` against the same solution's compilation round-trips correctly; `HasAncestor` finds a symbol two levels up the `Parent` chain and correctly returns false for an unrelated symbol.
- **Commit:** `add reference graph node model`

### 4. Engine: incoming references `[x]`

- **Files:** `RoslynQuery/ReferenceGraph/ReferenceGraphEngine.cs` (new), `RoslynQuery.Tests/ReferenceGraph/ReferenceGraphEngineIncomingTests.cs` (new)
- **Do:** `internal static class ReferenceGraphEngine` with `FindIncomingAsync(ISymbol target, Solution solution, IImmutableSet<Document> documents, ReferenceUsageKind filter, ReferenceGraphNode parent, CancellationToken)`. Call `SymbolFinder.FindReferencesAsync(target, solution, documents, cancellationToken)`. For every `ReferenceLocation` where `!IsCandidateLocation`, classify it with `ReferenceUsageClassifier.Classify`, skip if the result doesn't intersect `filter`, then find the enclosing declaration symbol via `SemanticModel.GetEnclosingSymbol` at that location - same walk as `ScopeResolver.ResolveDeclarationAsync` (`RoslynQuery/Query/ScopeResolver.cs:131`), stopping at the first symbol satisfying the same "declaration symbol" test as `ScopeResolver.IsDeclarationSymbol` (line 154). Group by that enclosing symbol into one `ReferenceGraphNode` per group (respecting `parent.HasAncestor` to mark `IsRecursive` and skip descending further into an already-visited ancestor), each carrying its group's `Locations` list. Cap at 200 nodes with a trailing "N more..." placeholder node.
- **Verify:** `dotnet build RoslynQuery.slnx -c Debug` succeeds, then `RoslynQuery.Tests/bin/Debug/net472/RoslynQuery.Tests.exe -class "RoslynQuery.Tests.ReferenceGraphEngineIncomingTests"` passes. Cover (multi-document `AdhocWorkspace` fixtures per `PredicateAwaitTests.cs:21-41`): a method called from two different methods produces two nodes both flagged `Invocation`; a field read in one method and written in another produces nodes flagged `Read` and `Write` respectively; restricting `documents` to a single document excludes a same-project caller in a different file that passing `null` (whole solution) would include; a type root's incoming set includes both a `new Foo()` site (`Construction`) and a parameter-typed-as-`Foo` site (`TypeReference`) when the filter includes both kinds, and excludes the `TypeReference` one when the filter doesn't.
- **Commit:** `add reference graph engine incoming path`

### 5. Engine: outgoing references `[x]`

- **Files:** `RoslynQuery/ReferenceGraph/ReferenceGraphEngine.cs` (extend), `RoslynQuery.Tests/ReferenceGraph/ReferenceGraphEngineOutgoingTests.cs` (new)
- **Do:** Add `FindOutgoingAsync(ISymbol root, Solution solution, ReferenceUsageKind filter, ReferenceGraphNode parent, CancellationToken)`. For a member root: union `root.DeclaringSyntaxReferences` (partial methods/types), get each `SemanticModel`, walk descendant nodes (include constructor initializers and property accessor bodies) the way `QueryEngine.ScanNodesAsync` walks a tree (`RoslynQuery/Query/QueryEngine.cs:180`), call `GetSymbolInfo` on each candidate node, classify with the same `ReferenceUsageClassifier.Classify`, skip anything outside `filter`, group by target symbol into one `ReferenceGraphNode` per group (same `HasAncestor`/`IsRecursive`/200-cap handling as step 4). For a type root: union this same walk over every member's declaring syntax plus the type's own `BaseListSyntax` (classified `TypeReference`), since a type has no single body of its own.
- **Verify:** `dotnet build RoslynQuery.slnx -c Debug` succeeds, then `RoslynQuery.Tests/bin/Debug/net472/RoslynQuery.Tests.exe -class "RoslynQuery.Tests.ReferenceGraphEngineOutgoingTests"` passes. Cover: a method that calls two other methods produces two `Invocation` nodes; a method that reads and writes two different fields produces correctly-flagged nodes; a directly self-recursive method's outgoing set marks the self-entry `IsRecursive` and does not attempt to expand it further; a partial method's outgoing set unions references from both partial declarations; a type root's outgoing set includes a reference made only inside one of its members plus its base type.
- **Commit:** `add reference graph engine outgoing path`

### 6. Symbol glyph converter `[x]`

- **Files:** `RoslynQuery/ToolWindow/SymbolGlyphMonikerConverter.cs` (new), `RoslynQuery.Tests/ToolWindow/SymbolGlyphMonikerConverterTests.cs` (new)
- **Do:** `IValueConverter` mapping the `SymbolGlyph` enum added in step 3 (`Method`, `Constructor`, `Property`, `Field`, `Event`, `Constant`, `EnumMember`, `Class`, `Structure`, `Interface`, `Enumeration`, `Delegate`, `Branch`, `Unknown`) to `Microsoft.VisualStudio.Imaging.Interop.ImageMoniker` values from `KnownMonikers` - same shape as `RoslynQuery/ToolWindow/TargetMonikerConverter.cs`. The `SymbolKind`/`MethodKind`/`TypeKind` collapsing already happened in `SymbolGlyphs.For`, so the converter stays a flat enum switch.
- **Verify:** `dotnet build RoslynQuery.slnx -c Debug` succeeds, then `RoslynQuery.Tests/bin/Debug/net472/RoslynQuery.Tests.exe -class "RoslynQuery.Tests.SymbolGlyphMonikerConverterTests"` passes. `Convert` is a pure function over enum inputs - test it directly without any WPF/UI host.
- **Commit:** `add symbol glyph moniker converter`

### 7. Tool window UI `[x]`

- **Files:** `RoslynQuery/ToolWindow/ReferenceGraphToolWindow.cs` (new), `RoslynQuery/ToolWindow/ReferenceGraphToolWindowControl.xaml` (new), `RoslynQuery/ToolWindow/ReferenceGraphToolWindowControl.xaml.cs` (new)
- **Do:** `ReferenceGraphToolWindow` mirrors `QueryToolWindow.cs` (new GUID, `Caption = "Reference Graph"`). `ReferenceGraphToolWindowControl.xaml` mirrors `QueryToolWindowControl.xaml`'s theme brushes and `ThemedDialog*StyleKey` styles. Toolbar: scope `ComboBox` (Current Document / Current Project / My Solution, default Current Project, tooltip noting it only affects "References To"), Refresh button (re-expands every currently-expanded node), Stop button (cancels the shared `CancellationTokenSource`, same field idiom as `QueryToolWindowControl._cancellation`), Clear/trash button (empties the root `ObservableCollection<ReferenceGraphNode>`), and a "Filter" `ToggleButton` opening a `Popup` (`StaysOpen="True"`, `IsOpen` bound to the toggle's `IsChecked`) containing one `CheckBox` per `ReferenceUsageKind` flag - changing any checkbox re-expands every currently-expanded node via the same refresh path as the Refresh button. Body: a `TreeView` bound to the root collection with a `HierarchicalDataTemplate` over `Children`; a node's `Expanded` event (or a `IsExpanded` property setter) triggers the lazy `ReferenceGraphEngine.FindIncomingAsync`/`FindOutgoingAsync` call on a background thread (`ThreadHelper.JoinableTaskFactory.RunAsync(...).FileAndForget(...)` -> `TaskScheduler.Default` -> `Dispatcher.BeginInvoke(DispatcherPriority.Background, ...)` to swap the placeholder child for real results, same shape as `QueryToolWindowControl.Run`/`RunCoreAsync`, lines 231-347). Double-click a node navigates via `SpanMapper.ResolveAsync` + `DocumentNavigator.Navigate`, same as `OnResultDoubleClick` (lines 148-170). Each "root" invocation (a public method the package command handlers will call) resolves the target `ISymbol` (via `SymbolResolver`), builds a new root `ReferenceGraphNode` with two synthetic children ("References To 'X'", "References From 'X'"), and prepends it to the root collection.
- **Verify:** `dotnet build RoslynQuery.slnx -c Debug` succeeds (XAML compiles, no runtime UI test at this step - deferred to step 8's manual smoke test).
- **Commit:** `add reference graph tool window ui`

### 8. Commands, package wiring, and smoke test `[~]`

- **Files:** `RoslynQuery/RoslynQueryPackage.vsct` (extend), `RoslynQuery/RoslynQueryPackage.cs` (extend)
- **Do:** In the `.vsct`, add a `<Button>` under `IDG_VS_WNDO_OTRWNDWS1` for "Reference Graph" (View > Other Windows), mirroring the existing `cmdidShowQueryToolWindow` button (`RoslynQuery/RoslynQueryPackage.vsct:11`), plus a `View Reference Graph` button in the editor's code-window context menu group (check `vsshlids.h`/`stdidcmd.h` for the exact `IDG_VS_CTXT_CODEWIN_*` group real "Go To Definition" lives in, and use that). In `RoslynQueryPackage.cs`, add `[ProvideToolWindow(typeof(ReferenceGraphToolWindow), Style = VsDockStyle.Tabbed, Window = ...)]` alongside the existing attribute (line 20), wire the "open blank window" command the same way `ShowToolWindowCommandId` is wired (lines 33-41), and wire the context-menu command with a synchronous, cheap `BeforeQueryStatus` (enabled whenever `ScopeResolver.GetActiveContext` finds an active C# view - do not attempt semantic symbol resolution on the UI thread) whose invoke handler resolves the caret symbol (`SymbolResolver.ResolveAtCaretAsync`) off the UI thread and, on success, shows the tool window and roots a new graph on it; on failure (no resolvable symbol), show the tool window with an error line via the same `SetError` pattern `QueryToolWindowControl` already uses (`RoslynQuery/ToolWindow/QueryToolWindowControl.xaml.cs:225-229`).
- **Verify:** `dotnet build RoslynQuery.slnx -c Debug` succeeds, then manually launch the VS experimental instance (F5 on the `RoslynQuery` project) and: right-click a method with known callers/callees in a test solution -> View Reference Graph; confirm both "References To" and "References From" populate; expand a few levels including into a recursive method and confirm it terminates cleanly with an `IsRecursive` marker instead of looping; toggle the filter flyout's `TypeReference` checkbox and confirm the tree refreshes to include/exclude type-usage nodes; double-click a node in each direction and confirm navigation lands on the correct line; open the window from View > Other Windows with no prior invocation and confirm it opens blank without error.
- **Progress:** Code complete and `dotnet build RoslynQuery.slnx -c Debug` succeeds with no warnings.
  The `.vsct` gained `cmdidShowReferenceGraphToolWindow` (0x0101, View > Other Windows) and
  `cmdidViewReferenceGraph` (0x0102, editor context menu under `IDG_VS_CODEWIN_NAVIGATETOLOCATION`);
  `RoslynQueryPackage` gained the second `[ProvideToolWindow]`, both command registrations, and the
  synchronous `BeforeQueryStatus`. **What is left is only the manual F5 smoke test** in the experimental
  instance - it needs a human at a running Visual Studio and cannot be automated from here. Run the
  checklist under **Verify** below; if it all passes, flip this step to `[x]`.
- **Commit:** `wire up reference graph commands and tool window registration`

### 9. README documentation `[x]`

- **Files:** `README.md` (extend)
- **Do:** Add a "Reference Graph" section mirroring the structure of the existing "Roslyn Query" section: how to open it (View > Other Windows, or right-click a member/type -> View Reference Graph), what the two branches mean, the scope combo's incoming-only scope, and the usage-kind filter flyout. No C# code fences in the new section (or if any are added, they must still satisfy `RoslynQuery.Tests/ReadmeExampleTests.cs`, which compiles every README code fence as a test).
- **Verify:** `dotnet build RoslynQuery.slnx -c Debug` succeeds, then `RoslynQuery.Tests/bin/Debug/net472/RoslynQuery.Tests.exe -class "RoslynQuery.Tests.ReadmeExampleTests"` still passes.
- **Commit:** `document reference graph window in readme`

## Steps added after the plan was written

These come from smoke-test feedback and are user-directed changes to the original design. Steps 10-12
supersede **Non-goal 1** ("no per-call-site leaf level under a node"), which the user reversed.

### 10. Collapse duplicate occurrences from linked documents `[x]`

- **Files:** `RoslynQuery/ReferenceGraph/ReferenceLocationInfo.cs`, `SymbolIdentity.cs`,
  `ReferenceGraphEngine.cs`, `RoslynQuery.Tests/Infrastructure/TestSolutions.cs`,
  `RoslynQuery.Tests/ReferenceGraph/ReferenceGraphEngineLinkedFileTests.cs` (new)
- **Do:** `ReferenceLocationInfo` carries the file path, line and column. `GroupSet.Add` keys
  occurrences on (file path, span) and ORs the kinds of a repeat instead of appending a second entry.
  `SymbolIdentity` equality drops `ProjectId`. Locations sort by file then position.
- **Verify:** `RoslynQuery.Tests.exe -class "RoslynQuery.Tests.ReferenceGraphEngineLinkedFileTests"`.
- **Commit:** `collapse duplicate reference occurrences from linked documents`

### 11. Per-location child rows `[x]`

- **Files:** `RoslynQuery/ReferenceGraph/ReferenceGraphNode.cs`, `SymbolGlyph.cs`,
  `RoslynQuery/ToolWindow/SymbolGlyphMonikerConverter.cs`,
  `RoslynQuery.Tests/ReferenceGraph/ReferenceGraphNodeLocationRowTests.cs` (new)
- **Do:** `SetChildren` prepends a synthetic "Locations (N)" branch to any row backed by more than one
  occurrence, holding one navigable leaf per occurrence (`FileName (line,col)`), ahead of the graph
  rows. The branch is built already populated and is not `IsExpandable`, so the lazy fetch leaves it
  alone. A single-occurrence row gets no branch - the row itself already navigates there.
- **Verify:** `RoslynQuery.Tests.exe -class "RoslynQuery.Tests.ReferenceGraphNodeLocationRowTests"`.
- **Commit:** `add navigable rows for each reference location`

### 13. Filter changes no longer wipe the tree `[x]`

- **Files:** `RoslynQuery/ReferenceGraph/ReferenceGraphNode.cs`,
  `RoslynQuery/ToolWindow/ReferenceGraphToolWindowControl.xaml.cs`,
  `RoslynQuery.Tests/ReferenceGraph/ReferenceGraphRefreshTests.cs` (new)
- **Do:** Root construction moved into `ReferenceGraphNode.CreateRoot`, which builds the root
  **not** `IsExpandable`. `ShallowestExpanded` moved onto the node too, so the refresh walk is
  reachable from tests.
- **Verify:** `RoslynQuery.Tests.exe -class "RoslynQuery.Tests.ReferenceGraphRefreshTests"`.
- **Commit:** folded into the step 12 commit - both fixes touch the same file, and splitting them
  would have left a commit that does not compile.

### 12. Double-click navigates without toggling `[~]` (second attempt)

- **Files:** `RoslynQuery/ToolWindow/ReferenceGraphToolWindowControl.xaml.cs`
- **Do:** `OnNodeDoubleClick` sets `e.Handled = true` when it navigates. `TreeViewItem` toggles
  `IsExpanded` from its `MouseLeftButtonDown` class handler, and `MouseDoubleClick` is raised while
  `MouseDown` is still routing; WPF promotes `MouseDown` to `MouseLeftButtonDown` only when it comes
  back unhandled, so handling it suppresses the toggle. A branch row (no `DocumentId`) is left
  unhandled on purpose, so double-clicking one still expands it.
- **Progress:** First attempt (handling `MouseDoubleClick`) was reported still toggling. Now hooks the
  tunnelling `PreviewMouseLeftButtonDown` on the TreeView, which runs before any `TreeViewItem` sees
  the input, plus an `IsExpanded` restore posted at `DispatcherPriority.Input` as a fallback that makes
  the final state correct even if the suppression fails again. Still **not verified** - no WPF host in
  the test suite.
- **Commit:** `stop double-click from toggling row expansion`

## Deviations

- **Status field records the commit subject, not the hash.** Rule 5 requires the code change and this
  file's update to land in one commit, so a hash recorded in that same commit can never be its own.
  The field is `Last synced commit subject` instead; check it with `git log -1 --format=%s`.
- **Step 1: `++`/`--` classify as `Write` only.** Followed the step's literal rule rather than the
  "genuinely ambiguous" latitude. Increment does read, but the filter is more useful when a mutation
  shows up under `Write` alone; compound assignment stays `Read | Write` as specified.
- **Step 1: type-position detection keys off the target symbol first.** A syntactic ancestor walk
  alone misses cases and duplicates what the binder already knows, so `Classify` returns
  `TypeReference` whenever the target is an `ITypeSymbol`/`INamespaceSymbol` (after the construction
  check), with the ancestor walk kept only as a fallback for occurrences that did not bind.
- **Step 1 extra: `+=`/`-=` on an `IEventSymbol` classifies as `Write`, not `Read | Write`.** A
  subscription is not a read-modify-write of a value.
- **Step 9: the README intro was rewritten too.** It described a single tool window; it now names both
  and points at how each is opened. The new section adds no C# fences, so `ReadmeExampleTests`
  (a hardcoded list, not a scanner) is unaffected - re-run and still green.
- **Step 8: the context-menu button is `DynamicVisibility` + `DefaultDisabled`.** `BeforeQueryStatus`
  enables it only when there is an active view whose file is `.cs`, which is the cheapest test that
  matches the plan's "do not resolve symbols on the UI thread" constraint.
- **Step 8: the manual smoke test found a XAML crash on open; fixed.** `FilterToggle` is a
  `ToggleButton` but was given `ThemedDialogButtonStyleKey`, whose `TargetType` is `Button`, so opening
  the window threw `XamlParseException` -> `InvalidOperationException: 'Button' TargetType does not
  match type of element 'ToggleButton'`. Now uses `ThemedDialogToggleButtonStyleKey`, which exists
  alongside the Button/CheckBox/RadioButton/ComboBox keys (verified by reflecting over
  `VsResourceKeys` in Microsoft.VisualStudio.Shell.15.0 17.14). Every other styled element in the file
  was re-checked for the same mismatch; the rest are all type-matched. **A `TargetType` mismatch is
  invisible to the compiler and only surfaces when the control is constructed** - it is exactly the
  class of defect this step's manual smoke test exists to catch.
- **Step 8: the package needed rule-based background autoload.** "View Reference Graph" was greyed out
  on a first right-click unless a window had already been opened by hand: the button is
  `DefaultDisabled`, so it stays disabled until `BeforeQueryStatus` runs, and `BeforeQueryStatus`
  cannot run until the package is loaded - which nothing did except opening a window.
  `[ProvideAutoLoad]` + `[ProvideUIContextRule]` on an `ActiveEditorContentType:CSharp` term now load
  the package in the background whenever a C# editor is active, which is exactly the set of cases
  where the command is meant to be usable. Confirmed in the generated
  `RoslynQuery/bin/Debug/net472/RoslynQuery.pkgdef`: an `AutoLoadPackages\{c6829cab-...}` entry with
  `dword:00000002` (BackgroundLoad) and a `UIContextRules\{c6829cab-...}` entry carrying the term.
- **Step 13: changing the filter replaced a root's two branches with a bare incoming result.** The
  root was built with the default `expandable: true`, so the refresh walk saw it as an ordinary
  fetchable row: `BeginExpand(root)` ran `FindIncomingAsync` on it and `SetChildren` overwrote
  "References To" and "References From" with the incoming rows. `CreateRoot` now builds it
  `expandable: false` - its children are the two branches and nothing else - which makes the walk skip
  it and descend to the branches instead. Four tests in `ReferenceGraphRefreshTests` reproduce the
  failure (verified by flipping the flag back).
- **Step 12, second attempt: `MouseDoubleClick` was too late in the input chain.** `e.Handled` there did
  not stop `TreeViewItem.OnMouseLeftButtonDown` from toggling. The handler moved to
  `PreviewMouseLeftButtonDown` on the TreeView - tunnelling, so it runs before any item - and skips the
  expander chevron (a `ToggleButton`) and branch rows so those still expand on double-click. A restore
  of `IsExpanded` posted at `DispatcherPriority.Input` backs it up: a no-op when the suppression works,
  and a correction when it does not.
- **Step 12 is reasoned, not measured.** The `e.Handled = true` fix depends on WPF promoting
  `MouseDown` to `MouseLeftButtonDown` only when the former is unhandled. That is the same mechanism
  that makes handling `MouseDown` suppress `MouseLeftButtonDown` generally, but it is not something the
  test suite can exercise - there is no WPF host here. Confirm it in the smoke test.
- **Step 11 supersedes Non-goal 1.** The plan ruled out a per-call-site leaf level; the user reversed
  that after seeing a row report several references with no way to reach any but the first. The leaves
  sit under their own "Locations (N)" branch rather than being mixed in with the recursive graph rows,
  so a row's children stay one kind of thing.
- **Step 10: the same occurrence was reported once per project.** A multi-targeted project is several
  Roslyn projects over one set of files, so `SymbolFinder` returned each occurrence once per target
  framework. Because `SymbolIdentity` included the declaring `ProjectId`, the copies did not even merge
  into one row - a 4-TFM project produced four identical rows for every reference. Occurrences are now
  keyed on (file path, span), which is what actually identifies one place in the source, and
  `SymbolIdentity` compares on the declaration id alone. Verified against a 4-project fixture over one
  file path: 8 rows collapse to 2, each with one location.
  **This is not confirmed to be the cause of the reported "4 constructions" on a single `new()`** - see
  the note in **Open questions**. Probes over single-project fixtures showed every count correct
  (a collection initializer with three `new`s reports exactly "3 constructions").
- **Step 8: incoming rows came back in a different order on every refresh.** `SymbolFinder` searches
  documents in parallel, so `GroupSet`'s first-seen ordering was whatever the scheduler happened to do -
  the tree reshuffled on each refresh over an unchanged solution, which made the window untestable.
  `Build` now sorts incoming rows by display text (tie-broken on the declaration id, since two rows can
  share a signature across namespaces) **before** applying the 200-row cap, or which rows survived the
  cap would have been arbitrary too. Outgoing rows keep insertion order: that is already deterministic
  and is the order they appear in the source, which is more useful there than alphabetical.
  Each group's `Locations` list is sorted as well - the first location is what double-click navigates
  to, so it has to be the same one every time.
  Three regression tests were added; only `Incoming_RowOrder_IsSortedAndStableAcrossRuns` reproduces
  the failure deterministically (verified by reverting the sort). The other two - location order within
  a row, and the cap keeping the sorted prefix - pass either way at these fixture sizes, because
  `SymbolFinder` happens to return a single document's hits in source order. They are kept as cheap
  guards, not as proof.
- **Step 8: the manual smoke test is still outstanding past that first crash.** The window now
  constructs, but the rest of the checklist below has not been walked, so the step stays `[~]`.
- **Step 7: the filter popup uses `StaysOpen="False"`, not `True`.** `IsOpen` is bound two-way to the
  toggle's `IsChecked`, so an outside click closes the popup and unchecks the button together; with
  `StaysOpen="True"` the popup could only ever be dismissed by hitting the toggle again. Clicks on the
  checkboxes inside it do not close it either way.
- **Step 7: the incoming scope is anchored on the row's own symbol, not on the caret.** "Current
  document" means the document declaring the symbol being expanded, so a row means the same thing
  however long the window has been open and wherever the caret has since moved.
- **Step 7: Refresh re-runs only the shallowest expanded row on each path.** Its children are replaced
  wholesale, so re-running its descendants first would be thrown away.
- **Step 7 extra: `ReferenceGraphDisplay`.** The `SymbolDisplayFormat` moved out of
  `ReferenceGraphEngine` so a root row is spelled exactly like the child rows under it.
- **Step 7: node types stay `internal`.** WPF binds fine to public properties of internal types here -
  `QueryHit` and `CachedPredicateItem` already do it in the shipping window. Only the converter and the
  control itself are public, which is what compiled XAML actually requires.
- **Step 6: named `SymbolGlyphMonikerConverter`,** since it maps `SymbolGlyph` rather than `SymbolKind`.
- **Step 6: `KnownMonikers` has no constructor glyph.** Verified by reflecting over
  Microsoft.VisualStudio.ImageCatalog 17.14: `Constructor` does not exist. `NewClass` is used instead.
  `SymbolGlyph.Branch` was also split into `IncomingBranch`/`OutgoingBranch` so the two branch rows get
  `KnownMonikers.CallTo` and `CallFrom`.
- **Step 6: the test project now references the imaging packages directly.** `RoslynQuery.csproj` pulls
  the VS SDK with `ExcludeAssets="runtime"` so the VSIX never ships devenv's own assemblies, which left
  nothing on disk for the converter to bind to in-process. `Microsoft.VisualStudio.ImageCatalog` and
  `Microsoft.VisualStudio.Imaging.Interop.14.0.DesignTime` are referenced from the test project with
  runtime assets. This is why `TargetMonikerConverter` has no tests today.
- **Step 5: the outgoing walk binds name nodes, not every node.** `QueryEngine.ScanNodesAsync` visits
  every descendant, which here would bind `a.B.C()` three times over. The walk only considers
  `SimpleNameSyntax` plus the creation forms (`ObjectCreationExpressionSyntax`,
  `ImplicitObjectCreationExpressionSyntax`, `ConstructorInitializerSyntax`), which covers every symbol
  exactly once.
- **Step 5: `new Foo()` is reported as the constructor, not as the type.** The creation expression binds
  to the constructor, so the type name that is its `Type` is skipped to avoid two rows for one span.
  Type arguments inside it (`new List<Foo>()`) are still their own `TypeReference` rows.
- **Step 5: `var` is not an outgoing reference.** It binds to the inferred type, but the user never
  wrote that type - it was producing a duplicate row next to the constructor for `var f = new Foo();`.
- **Step 5: a type root walks its declarations but stops at nested types.** Walking each
  `TypeDeclarationSyntax` already covers the members and the base list the step asked for, and a nested
  type is its own row, so the walk does not descend into one.
- **Step 5: a field root also walks its declared type.** A field's `DeclaringSyntaxReferences` point at
  the `VariableDeclarator`, which does not carry the type, so `VariableDeclaration.Type` is walked too.
- **Step 5: reduced extension methods and constructed generics collapse to what they were built from**
  (`ReducedFrom`, then `OriginalDefinition`), so the row matches the identity stored on it.
- **Step 4: the enclosing declaration is found syntactically, not via `GetEnclosingSymbol`.** The step
  said to copy `ScopeResolver.ResolveDeclarationAsync`'s walk, but the binder answers "the containing
  type" for every occurrence outside a body - a parameter's type, a return type, an attribute - so
  `void Accept(Foo f)` came back attributed to `Uses` rather than to `Uses.Accept`. `EnclosingDeclaration`
  now climbs the occurrence's ancestors to the first `MemberDeclarationSyntax` / `AccessorDeclarationSyntax` /
  `VariableDeclaratorSyntax` that declares a symbol, and only falls back to `GetEnclosingSymbol`. Lambdas
  and local functions are not member declarations, so they are stepped over for free.
- **Step 4: accessors roll up to their property or event.** `Normalize` maps a symbol with an
  `AssociatedSymbol` to that symbol, so a reference from a getter shows as a row for the property,
  matching Call Hierarchy and the plan's list of root kinds.
- **Step 4: recursive nodes are built non-expandable.** `IsRecursive` marks them and `expandable: false`
  keeps them from seeding a placeholder child that could never be filled.
- **Step 3: `SymbolKey` is internal - replaced by `SymbolIdentity` over `DocumentationCommentId`.** The
  throwaway probe the step called for showed `Microsoft.CodeAnalysis.SymbolKey` and
  `SymbolKeyResolution` are both non-public in Microsoft.CodeAnalysis.Workspaces 5.6.0, so the plan's
  original identity design is not implementable against public API (and reflection is out: devenv
  redirects Roslyn to its own build at runtime). `SymbolIdentity` stores the declaring `ProjectId` plus
  `DocumentationCommentId.CreateDeclarationId(symbol.OriginalDefinition)` and resolves via
  `GetFirstSymbolForDeclarationId` against the project's compilation. The probe confirmed round-trips
  for every supported root kind - types, nested types, fields, properties, events, constructors,
  overloads (the signature is part of the id), generic methods - plus resolution across a changed
  compilation snapshot. Constructed generics collapse to their definition, which is what the graph
  wants anyway.
- **Step 3 extra: `SymbolGlyph` enum + `SymbolGlyphs.For(ISymbol)`.** The node has to survive without a
  live `ISymbol`, so the icon is decided once at construction. Step 6's converter was restated to map
  this enum instead of `SymbolKind`/`MethodKind`.
- **Step 3: `ReferenceLocationInfo` struct instead of the planned value tuple.** Same three fields, but
  named - the list is threaded through four files.
- **Step 3 extra: `ReferenceGraphNode.Describe`.** Builds the secondary line ("3 refs (1 invocation,
  1 read, 1 construction)", or "2 invocations" when only one kind is present) next to the data it
  describes rather than in the XAML layer.
- **Step 3: `HasAncestor` includes the node itself.** Step 5 needs a directly self-recursive method to
  come back flagged, and that is a match on the node's own identity, not on a strict ancestor.
- **Test files moved into subfolders (user instruction, mid-step-2).** `ReferenceUsageClassifierTests.cs`
  moved from the test project root to `RoslynQuery.Tests/ReferenceGraph/`; later steps' file paths were
  retargeted the same way. See the folder convention under **Key files**.
- **Step 2 extra: `RoslynQuery.Tests/Infrastructure/TestSolutions.cs` added.** A shared `AdhocWorkspace`
  fixture (`Create`, `PathFor`, `Document`, `ExtractCaret`) instead of copying the `PredicateAwaitTests`
  boilerplate into each of the four new test files. Documents get real file paths because
  `Solution.GetDocumentIdsWithFilePath` is how the caret's document is found.
- **Step 2: the ancestor climb stops at the first node that binds to anything.** A caret on a local
  binds to an `ILocalSymbol`, so resolution returns null rather than climbing out to the enclosing
  method or, worse, to the call an argument sits in. The climb (capped at 4 levels) only runs for
  tokens that bind to nothing at all, so a caret on a brace still reaches its declaration.
- **Step 2 extra: `SymbolResolver.IsSupportedRoot` is public within the assembly**, so step 8's
  command handler can reuse the same root test rather than duplicating the kind switch.
- **Step 1: `ReferenceUsageKind.None = 0` added.** Needed so `default` and filter intersection
  (`(kind & filter) != ReferenceUsageKind.None`) have a name.

## Open questions

- **Is the reported "4 constructions" the linked-document bug fixed in step 10?** It reproduces as four
  duplicate *rows* rather than one row counting four, so either the observed solution multi-targets
  four ways (in which case step 10 fixes it) or there is a second cause still unfound. Needs the user
  to say whether the field's row appeared once or four times, and whether the project multi-targets.

- ~~Exact `IDG_VS_CTXT_CODEWIN_*` group ID for the editor context-menu command (step 8)~~ - **resolved:**
  `IDG_VS_CODEWIN_NAVIGATETOLOCATION` (0x02B1 in `vsshlids.h`), the group Go To Definition and Find All
  References live in, parented under `IDM_VS_CTXT_CODEWIN`.
- ~~Exact `SymbolKey.Create`/`.Resolve` overload signatures (step 3)~~ - **resolved:** `SymbolKey` is internal, `DocumentationCommentId` is used instead. See **Deviations**.
