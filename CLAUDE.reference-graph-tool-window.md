# Reference Graph tool window

<!-- RESUME PROTOCOL - any agent opening this file must follow this section before touching code. -->

## Resume protocol

You are resuming work described by this file. This file is the single source of truth for progress.

1. Read this entire file, then read every file listed in **Key files** and in the current step.
2. Run `git status` and `git log --oneline -5`. The working tree should be clean and `HEAD` should match the commit recorded in **Status**. If it does, this file is up to date; trust it and continue at the current step without re-auditing.
3. If the tree is dirty or `HEAD` does not match, reconcile first: figure out what happened, fix this file, commit the fix, then continue.
4. Work the first step that is not `[x]`. One step at a time.
5. **Every step ends with a commit that contains both the code change and the update to this file**, unless
   rule 5a applies. This is what makes the file trustworthy.
5a. **Commit before running anything that rewrites files in bulk** - a line-ending normalization pass, a
   patch script, a formatter, a bulk rename. Land the code as soon as it builds and its tests pass, run the
   bulk operation, then commit that separately and update this file. Splitting a step across two or three
   commits is always preferred to letting an unverified bulk edit sit on top of uncommitted work. This rule
   exists because a normalization one-liner of the form `open(p,'wb').write(open(p,'rb').read())` truncated
   three files to zero bytes - the write handle opens, and truncates, before the read is evaluated - and two
   of them were uncommitted. Never read and write one path in a single expression.
6. Commit messages: one terse line, imperative, lowercase, no body, no trailing period. Example: `add reference usage classifier`.
7. `git commit` only. **Never** `git push`, `git commit --amend`, `git rebase`, or `git reset --hard` unless explicitly told to.
8. Never mark a step `[x]` before its **Verify** command has actually run and passed. If it fails, the step stays `[~]` and the failure goes in **Deviations**.
9. If reality diverges from the plan (a step is wrong, impossible, or unnecessary), amend the steps here and log it in **Deviations** in the same commit. Never silently deviate.
10. If a turn ends mid-step, the step stays `[~]` with a `Progress:` line describing exactly where it stopped and what is left. Commit whatever is coherent; if nothing is coherent, still update `Progress:` and commit only this file.
11. Do not ask for a plan-freshness check. Assume it is fresh unless step 2 says otherwise.

Step states: `[ ]` not started, `[~]` in progress, `[x]` done, `[!]` blocked, `[-]` dropped.

## Status

- **State:** in-progress - phase 1 code complete; phase 2 (steps 15-26) planned, not started
- **Current step:** 18 - kind-narrowed incoming analyzers. Then 19-26 in order, then 27-28, which
  were added mid-phase and depend on the analyzer plumbing steps 16-20 build. Steps 8, 12 and 14 stay
  `[~]`: their manual smoke test is deliberately deferred into step 26, which rewrites the tree they were
  verifying, and step 26 is re-run once 28 lands.
- **Branch:** feature/favorites
- **Base commit:** e1c9fd34b4185a1f071a2fc0c9da3e0f51643a15
- **Last synced commit subject:** `add hierarchy analyzers to the reference graph engine` (verify with `git log -1 --format=%s`)
- **Last updated:** 2026-09-10

## Goal

A second VSIX tool window, "Reference Graph", alongside the existing "Roslyn Query" window. Right-clicking a method, constructor, property, field, event, or type in the editor (or opening the window from View > Other Windows) roots a lazily-expandable tree with two branches: "References To 'X'" (who references X) and "References From 'X'" (what X references). Each further node expands the same way, recursively, in whichever direction its branch started in. Styled like VS's built-in Call Hierarchy window but generalized to all reference kinds, not just calls. Done = builds clean, engine unit tests pass, and a manual F5 smoke test in the experimental instance shows both branches populating, recursion terminating cleanly, the usage-kind filter flyout live-refreshing the tree, and double-click navigation landing on the right line.

**Phase 2 supersedes this goal.** The window keeps its purpose but changes shape: the two directional
branches become ILSpy's per-symbol-kind analyzer set, and every row re-analyses rather than inheriting a
direction. See **Phase 2: ILSpy-style analyzers** below; where the two disagree, phase 2 wins.

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

### 14. Enter navigates from the selected row `[~]`

- **Files:** `RoslynQuery/ToolWindow/ReferenceGraphToolWindowControl.xaml` / `.xaml.cs`, `README.md`
- **Do:** `KeyDown` on the TreeView: Enter on a selected row with a `DocumentId` navigates through the
  same `Navigate` path double-click uses, and marks the event handled. A row with nowhere to go is left
  unhandled so the TreeView keeps its own behaviour. README updated for Enter and for the
  `Locations (N)` branch added in step 11, which its double-click paragraph still predated.
- **Progress:** Builds clean, `ReadmeExampleTests` still passes. Keyboard handling has no WPF host in
  the test suite, so confirm in the smoke test.
- **Commit:** `navigate on enter from the selected row`

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

## Phase 2: ILSpy-style analyzers

Steps 15-26 rework the window from two fixed directional branches into ILSpy's "Analyze symbol"
model. User-directed, after comparing the window against ILSpy's Analyzer pane side by side. These
decisions were taken up front and are not open for re-litigation inside the steps:

1. **Full analyzer model.** The `References To` / `References From` pair is replaced by a per-symbol-kind
   set of semantic branches (`Uses`, `Used By`, `Overrides`, `Overridden By`, `Implements`,
   `Implemented By`, `Instantiated By`, `Exposed By`, `Read By`, `Assigned By`, `Derived Types`,
   `Extension Methods`, `Applied To`).
2. **Direction stops sticking.** Every symbol row re-offers the full applicable set for its own symbol,
   the way ILSpy does. `ReferenceDirection` ceases to be a node property.
3. **The usage-kind filter flyout is dropped.** `Used By` / `Read By` / `Assigned By` /
   `Instantiated By` / `Exposed By` already carry the kind, so a second cross-cutting filter is
   redundant. `ReferenceUsageKind` survives as an internal engine concept; only the UI control goes.
4. **Signature rows are syntax-coloured** and spelled ILSpy's way: namespace-qualified, with the return
   type appended as `: T`.
5. **Metadata depth is taken where it is free, and not bought where it is not.** The hierarchy analyzers
   already cross into referenced assemblies at no cost (see the probe findings); `Uses` and the incoming
   analyzers stop at the source boundary and say so. IL-level analysis is **deferred**: step 24 adds the
   settings that will one day switch it on, deliberately disabled and non-functional.
6. **No Just My Code toggle.** Considered and dropped; the options page from step 24 is where that kind
   of switch belongs if it is ever wanted.

### Shape of the tree

The tree strictly alternates symbol rows and analyzer branch rows, which is what makes ILSpy's
recursion work:

```
System.Random.ThreadSafeRandom.NextBytes(byte[]) : void   symbol (root)
  Uses (3 in 2 ms)                                        analyzer branch
    System.ThrowHelper.ThrowArgumentNullException(...)    symbol
      Locations (2)                                       locations branch
      Uses (2 in 3 ms)                                    analyzer branch
      Used By (7 in 5 ms)                                 analyzer branch
```

Expanding a symbol row no longer runs a fetch: it materialises that symbol's applicable branch rows,
and each of those fetches lazily when expanded. `Locations (N)` stays (step 11's addition, which
ILSpy has no equivalent for because it has no source spans to offer) and sorts ahead of the analyzer
branches.

### Probe findings these steps depend on

Measured against Microsoft.CodeAnalysis.Workspaces 5.6.0 and ICSharpCode.Decompiler 11.0.0.9375 with
throwaway console apps, not taken from documentation. Do not re-derive these; they are why the steps
below are shaped the way they are.

**Reference and hierarchy finding**

- `SymbolFinder.FindOverridesAsync` is **transitive**: on a virtual `Base.Draw` with `Middle.Draw`
  overriding it and `Leaf.Draw` overriding that, it returns both. One call, no manual walk.
- `FindOverridesAsync` returns **empty for an interface member**. `Implemented By` is a different call
  (`FindImplementationsAsync`), not a special case of the same one.
- `FindImplementationsAsync(ISymbol, ...)` on `IShape.Area` returns the first implementing member per
  type (`Base.Area`, `Direct.Area`), not the transitive override closure.
- `FindImplementedInterfaceMembersAsync` returns **empty for an override**: `Leaf.Area` (which overrides
  `Middle.Area`, which overrides the implementing `Base.Area`) gives `[]`, while `Base.Area` gives
  `IShape.Area`. `Implements` must therefore walk `OverriddenMethod`/`OverriddenProperty`/`OverriddenEvent`
  to the root of the chain **before** calling it.
- `FindDerivedClassesAsync`'s three-argument overload is transitive; the `transitive: false` overload
  returns direct subclasses only. `FindImplementationsAsync(INamedTypeSymbol, transitive: true)` returns
  every implementing type including ones that inherit the implementation.
- `IMethodSymbol.ReduceExtensionMethod(type)` returns null when the extension does not apply, which is
  the whole `Extension Methods` test. There is no dedicated finder API;
  `FindSourceDeclarationsAsync(solution, _ => true, SymbolFilter.Member)` plus that check is the scan.
- **An attribute application binds to the attribute's constructor, not to its type.** `[Marker]` reports
  as a reference to `MarkerAttribute.MarkerAttribute()`. `FindReferencesAsync` on the type does cascade
  to the constructors, so the locations are reachable, but `Applied To` has to recognise them
  syntactically (occurrence inside an `AttributeSyntax`) rather than by target symbol.

**How deep the graph can go**

- **The hierarchy finders search referenced assemblies, not only source.** `FindOverridesAsync(Stream.Read)`
  returns `BufferedStream.Read`, `FileStream.Read`, `MemoryStream.Read`, `UnmanagedMemoryStream.Read`,
  `CryptoStream.Read` and `IsolatedStorageFileStream.Read` alongside the one source override.
  `FindDerivedClassesAsync(Stream, transitive: true)` does the same, and
  `FindImplementationsAsync(IDisposable)` returns roughly 110 framework types. So `Overrides`,
  `Overridden By`, `Implements`, `Implemented By` and `Derived Types` reach ILSpy-grade depth for free.
- **Metadata symbols round-trip through `SymbolIdentity` unchanged.**
  `M:System.String.Format(System.String,System.Object)~System.String` resolves back to the symbol, so a
  framework row stays expandable rather than going inert.
- **`FindReferencesAsync` works on a metadata symbol** and returns the source callers, so `Used By`
  rooted on a framework symbol is meaningful (it answers "who in my solution calls this"). It will never
  return a framework caller.
- **`Uses` is the one unavoidable dead end.** A metadata symbol has `DeclaringSyntaxReferences.Length == 0`,
  so the syntax walk has nothing to walk. This is what step 21 has to state honestly in the UI.
- Roslyn 5.6.0 exposes **no public MetadataAsSource or decompilation type** in `Features`,
  `CSharp.Features` or `Workspaces`, so "decompile to C# and re-run the ordinary analysis" is closed.
- The PE files are on disk and reachable (`PortableExecutableReference.FilePath` gives
  `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\mscorlib.dll`), and `MetadataFile.GetMethodBody`
  hands back a `MethodBodyBlock` with an IL reader. IL-level analysis is therefore physically possible;
  it is deferred for effort reasons, not capability ones.

**Decompilation (step 25)**

- ICSharpCode.Decompiler 11.0.0.9375 (ILSpy's own engine, so output matches what ILSpy shows) produces
  **real method bodies**, not signature stubs.
- The bridge is exact and needs no new identity concept: `SymbolIdentity.DeclarationId` already holds a
  documentation comment id, `IdStringProvider.FindEntity(id, new SimpleTypeResolveContext(module))`
  turns it into an `IEntity`, `IEntity.MetadataToken` is the `EntityHandle`, and
  `CSharpDecompiler.DecompileAsString(handle)` returns the member. `IdStringProvider.GetIdString`
  round-trips it back to the identical id.
- Cost, measured on mscorlib: ~120 ms once to construct the decompiler for an assembly, then 0-9 ms per
  member. A whole type is 216 ms for `System.String` (200 KB of output) and 1.9 s for a cold
  `MemoryStream` including construction. Per-assembly caching makes this comfortably interactive.
- **Assembly unification is the real risk, not the API.** The probe hard-failed at runtime with
  `FileLoadException: System.Memory, Version=4.0.2.0` until `System.Memory` was pinned to 4.6.3;
  the transitively-resolved 4.5.5 ships assembly version 4.0.1.2. In-proc this is worse, because devenv
  already has its own `System.Memory`, `System.Collections.Immutable` and `System.Reflection.Metadata`
  loaded under its own redirects. Step 25 therefore **starts** by proving the decompiler loads and runs
  inside the experimental instance, before any UI is built on it.

### 15. Analyzer kinds and the applicability table `[x]`

- **Files:** `RoslynQuery/ReferenceGraph/ReferenceAnalyzerKind.cs` (new),
  `RoslynQuery/ReferenceGraph/ReferenceAnalyzers.cs` (new),
  `RoslynQuery.Tests/ReferenceGraph/ReferenceAnalyzersTests.cs` (new)
- **Do:** `internal enum ReferenceAnalyzerKind { Uses, UsedBy, ReadBy, AssignedBy, InstantiatedBy,
  ExposedBy, AppliedTo, Overrides, OverriddenBy, Implements, ImplementedBy, DerivedTypes,
  ExtensionMethods }` plus a `Header(this ReferenceAnalyzerKind)` giving ILSpy's exact wording
  (`"Used By"`, `"Instantiated By"`, ...). `ReferenceAnalyzers.For(ISymbol)` returns the applicable
  set **in display order**. Applicability is computed from the symbol, not just its kind - this is what
  keeps a static method down to `Uses` + `Used By` (screenshot 1) while an override gets five branches
  (screenshot 2):
  - ordinary method / property / event: `Uses`, `UsedBy`; `Overrides` when `IsOverride`;
    `OverriddenBy` when `IsVirtual || IsAbstract || IsOverride` and not `IsSealed`;
    `Implements` when the containing type has any interface whose members it satisfies;
    `ImplementedBy` when the symbol is itself an interface member.
  - constructor: `Uses`, `UsedBy`.
  - field: `Uses`, `ReadBy`, `AssignedBy`. A `const` or `readonly` field still gets `AssignedBy`
    (the initialiser and any constructor assignment are real writes).
  - class / struct: `Uses`, `UsedBy`, `InstantiatedBy`, `ExposedBy`, `DerivedTypes` (class only),
    `ExtensionMethods`; plus `AppliedTo` when the type derives from `System.Attribute`.
  - interface: `Uses`, `UsedBy`, `ExposedBy`, `DerivedTypes`, `ImplementedBy`, `ExtensionMethods`.
  - enum: `Uses`, `UsedBy`, `ExposedBy`, `ExtensionMethods`. delegate: adds `InstantiatedBy`.
- **Verify:** `dotnet build RoslynQuery.slnx -c Debug` succeeds, then
  `RoslynQuery.Tests/bin/Debug/net472/RoslynQuery.Tests.exe -class "RoslynQuery.Tests.ReferenceAnalyzersTests"`
  passes. Cover: a static method gets exactly `Uses`/`UsedBy`; an override gets `Overrides` and
  `OverriddenBy`; a sealed override gets `Overrides` but not `OverriddenBy`; an interface member gets
  `ImplementedBy` and not `OverriddenBy`; a class implementing an interface member gets `Implements`;
  an attribute class gets `AppliedTo` and a plain class does not; an interface gets `DerivedTypes` and
  `ImplementedBy` but not `InstantiatedBy`.
- **Commit:** `add reference analyzer kinds and applicability table`

### 16. Node roles and the alternating tree `[x]`

- **Files:** `RoslynQuery/ReferenceGraph/ReferenceGraphNode.cs`,
  `RoslynQuery/ReferenceGraph/ReferenceDirection.cs` (deleted),
  `RoslynQuery/ReferenceGraph/SymbolGlyph.cs`,
  `RoslynQuery.Tests/ReferenceGraph/ReferenceGraphNodeTests.cs`,
  `ReferenceGraphNodeLocationRowTests.cs`, `ReferenceGraphRefreshTests.cs`
- **Do:** Add `internal enum NodeRole { Symbol, Analyzer, Locations, Location, Message }` and carry it on
  the node. `ReferenceDirection` is deleted; a node's `Analyzer` (a `ReferenceAnalyzerKind?`) replaces
  it, set only on `Analyzer` rows. `CreateRoot` builds a `Symbol` row whose children are
  `ReferenceAnalyzers.For(symbol)` mapped to `Analyzer` rows. `ExpandSymbol(node, symbol)` does the same
  for any symbol row, prepending the `Locations (N)` branch when the row has more than one occurrence.
  `SymbolGlyph` also gains `Operator` (covering `MethodKind.UserDefinedOperator` and
  `MethodKind.Conversion`), which step 15's coverage showed collapsing into the generic method icon while
  ILSpy gives operators their own - visible on `System.UInt128.operator *` and `System.TimeSpan.operator *`
  in the reference screenshots - plus `Namespace`, `LocalFunction`, `Lambda`, `Local`, `Parameter` and
  `TypeParameter` for the root kinds steps 27 and 28 add.
  A `Symbol` row is expandable but its expansion is **synchronous** - it needs no fetch, only the
  applicability table - so `IsLoaded` is set the moment it materialises its branches. Only `Analyzer`
  rows fetch. `HasAncestor` walks `Parent` past `Analyzer` rows unchanged (it already compares
  identities, and branch rows carry their parent symbol's identity).
- **Verify:** `dotnet build RoslynQuery.slnx -c Debug` succeeds, then
  `RoslynQuery.Tests.exe -class "RoslynQuery.Tests.ReferenceGraphNode*"` and
  `-class "RoslynQuery.Tests.ReferenceGraphRefreshTests"` pass. Cover: a root's children are its
  applicable analyzer branches in order; expanding a symbol row two levels down produces branches, not
  results; `Locations (N)` sorts ahead of the branches; a recursive symbol row is built non-expandable
  and therefore offers no branches at all.
- **Commit:** `alternate symbol and analyzer rows in the reference graph`

### 17. Engine: hierarchy analyzers `[x]`

- **Files:** `RoslynQuery/ReferenceGraph/HierarchyAnalyzers.cs` (new),
  `RoslynQuery.Tests/ReferenceGraph/HierarchyAnalyzerTests.cs` (new)
- **Do:** `Overrides` walks the `OverriddenMethod`/`OverriddenProperty`/`OverriddenEvent` chain and
  returns every step of it, nearest first. `OverriddenBy` is one `SymbolFinder.FindOverridesAsync` call
  (already transitive). `Implements` walks the override chain **to its root first**, then calls
  `FindImplementedInterfaceMembersAsync` on that root - see the probe findings; calling it on the
  symbol as given returns nothing for any override. `ImplementedBy` is
  `FindImplementationsAsync(ISymbol, ...)`. `DerivedTypes` is `FindDerivedClassesAsync` for a class,
  `FindDerivedInterfacesAsync` for an interface, both transitive. For an interface, `ImplementedBy` is
  `FindImplementationsAsync(INamedTypeSymbol, transitive: true)`. A result declared in source becomes a
  row whose location is its declaration; a result from metadata has none, and step 21 handles that.
- **Verify:** `dotnet build RoslynQuery.slnx -c Debug` succeeds, then
  `RoslynQuery.Tests.exe -class "RoslynQuery.Tests.HierarchyAnalyzerTests"` passes. Cover, over the
  three-level `Base`/`Middle`/`Leaf` hierarchy the probe used: `OverriddenBy` on the base returns both
  descendants; `Overrides` on the leaf returns the middle then the base; `Implements` on an override
  two levels removed from the implementing member still finds the interface member (**this is the
  regression the probe exists to prevent** - verify it fails when the chain walk is removed);
  `ImplementedBy` on an interface member returns one row per implementing type; `DerivedTypes` on a
  class is transitive and on an interface returns derived interfaces; and, rooted on `System.IO.Stream`,
  `DerivedTypes` returns framework types, which is the free metadata depth this phase is built on.
- **Commit:** `add hierarchy analyzers to the reference graph engine`

### 18. Engine: kind-narrowed incoming analyzers `[ ]`

- **Files:** `RoslynQuery/ReferenceGraph/ReferenceGraphEngine.cs`,
  `RoslynQuery/ReferenceGraph/ReferenceUsageClassifier.cs`,
  `RoslynQuery.Tests/ReferenceGraph/IncomingAnalyzerTests.cs` (new)
- **Do:** `FindIncomingAsync` gains an occurrence predicate alongside its existing kind mask, and the
  six incoming analyzers become configurations of it: `UsedBy` = `Invocation | Read | Write`,
  `ReadBy` = `Read`, `AssignedBy` = `Write`, `InstantiatedBy` = `Construction`,
  `ExposedBy` = `TypeReference` **restricted to signature positions** (return type, parameter type,
  field/property/event type, base list, type-parameter constraint - not a local, not a cast, not a
  `typeof`), `AppliedTo` = any kind whose occurrence sits inside an `AttributeSyntax`. `AppliedTo` is
  the one that cannot key off the target symbol, because an attribute application binds to the
  constructor; the classifier gains an `IsAttributeApplication(SyntaxNode)` helper for it.
- **Verify:** `dotnet build RoslynQuery.slnx -c Debug` succeeds, then
  `RoslynQuery.Tests.exe -class "RoslynQuery.Tests.IncomingAnalyzerTests"` passes. Cover: a field read
  in one method and written in another lands in `ReadBy` and `AssignedBy` respectively and neither
  appears in the other; `InstantiatedBy` finds `new Foo()` but not `Foo x = null`; `ExposedBy` finds a
  parameter typed `Foo` and a property typed `Foo` but **not** a `typeof(Foo)` or a local declaration;
  `AppliedTo` on an attribute class finds both `[Marker] class` and `[Marker] field` sites and finds
  nothing for a non-attribute class.
- **Commit:** `narrow incoming references per analyzer kind`

### 19. Engine: extension methods `[ ]`

- **Files:** `RoslynQuery/ReferenceGraph/HierarchyAnalyzers.cs`,
  `RoslynQuery.Tests/ReferenceGraph/ExtensionMethodAnalyzerTests.cs` (new)
- **Do:** `FindExtensionMethodsAsync(INamedTypeSymbol, Solution, ...)`:
  `FindSourceDeclarationsAsync(solution, _ => true, SymbolFilter.Member)`, keep
  `IMethodSymbol { IsExtensionMethod: true }` whose `ReduceExtensionMethod(type)` is non-null, and
  return the **unreduced** definition as the row (the reduced form has no declaration to navigate to).
  This is a whole-solution declaration scan with no cheaper API available, and it is source-only, so it
  is both the branch step 20's timing display exists for and one step 21 has to mark as source-scoped.
- **Verify:** `dotnet build RoslynQuery.slnx -c Debug` succeeds, then
  `RoslynQuery.Tests.exe -class "RoslynQuery.Tests.ExtensionMethodAnalyzerTests"` passes. Cover: an
  extension on the type itself is found; an extension on an interface the type implements is found; an
  extension on an unrelated type is not; a generic extension constrained away from the type is not.
- **Commit:** `add extension method analyzer`

### 20. Analyzer dispatch, counts and timings `[ ]`

- **Files:** `RoslynQuery/ReferenceGraph/ReferenceGraphEngine.cs`,
  `RoslynQuery/ReferenceGraph/ReferenceGraphNode.cs`,
  `RoslynQuery.Tests/ReferenceGraph/AnalyzerDispatchTests.cs` (new)
- **Do:** One entry point - `RunAsync(ReferenceAnalyzerKind, ISymbol, Solution, IImmutableSet<Document>,
  ReferenceGraphNode, CancellationToken)` - switching to the right finder, so the UI has a single call
  site rather than a switch of its own. The branch row records `Stopwatch` elapsed milliseconds and the
  row count, and formats `SecondaryText` as ILSpy does: `(5 in 12 ms)`. A branch that returns nothing
  gets **no child rows at all** (not a `No references.` message row), which is what removes its
  expander and matches ILSpy's bare `Implements` header.
- **Verify:** `dotnet build RoslynQuery.slnx -c Debug` succeeds, then
  `RoslynQuery.Tests.exe -class "RoslynQuery.Tests.AnalyzerDispatchTests"` passes. Cover: every
  `ReferenceAnalyzerKind` value dispatches without throwing (a loop over `Enum.GetValues`, so a new kind
  added later cannot be silently unhandled); an empty result leaves `Children` empty and `IsLoaded`
  true; the count in `SecondaryText` matches `Children.Count` excluding the locations branch.
- **Commit:** `dispatch analyzers through one entry point and report counts`

### 21. Metadata rows: identity, glyphs, and honest dead ends `[ ]`

- **Files:** `RoslynQuery/ReferenceGraph/SymbolIdentity.cs`,
  `RoslynQuery/ReferenceGraph/ReferenceAnalyzers.cs`,
  `RoslynQuery/ReferenceGraph/ReferenceGraphNode.cs`,
  `RoslynQuery.Tests/ReferenceGraph/MetadataRowTests.cs` (new)
- **Do:** Step 17 starts returning framework symbols, which today only work by accident: `SymbolIdentity.Create`
  falls back to `fallbackProjectId` when a symbol has no declaring project, and resolution then happens to
  succeed because that project references the assembly. Make it deliberate - a metadata symbol records the
  project through which it was reached, and `ResolveAsync` documents that this is the project whose
  compilation must contain it. Add `IsFromMetadata` to the node (`symbol.Locations.All(l => l.IsInMetadata)`),
  give metadata rows a distinguishing glyph or suffix so a framework row is never mistaken for one of
  yours, and **suppress the branches that cannot answer for a metadata symbol**: `ReferenceAnalyzers.For`
  drops `Uses` (no syntax to walk) and keeps the incoming analyzers, whose results are real but
  source-scoped. The suppression is the honest form of the dead end; a branch that can only ever return
  nothing should not be offered.
- **Verify:** `dotnet build RoslynQuery.slnx -c Debug` succeeds, then
  `RoslynQuery.Tests.exe -class "RoslynQuery.Tests.MetadataRowTests"` passes. Cover: a metadata symbol's
  identity round-trips through `SymbolIdentity` and resolves against the reaching project; `For` on a
  metadata method omits `Uses` and retains `UsedBy`/`OverriddenBy`; `IsFromMetadata` is false for every
  source symbol in the fixture and true for `System.IO.Stream.Read`.
- **Commit:** `mark metadata rows and drop the branches they cannot answer`

### 22. UI: drop the filter, wire the alternating tree `[ ]`

- **Files:** `RoslynQuery/ToolWindow/ReferenceGraphToolWindowControl.xaml` / `.xaml.cs`
- **Do:** Delete the `Filter` toggle, its popup, all six checkboxes, `CurrentFilter` and `Flag`. The
  scope combo stays (it genuinely narrows the incoming analyzers) and its tooltip is retitled off
  `References To` onto the analyzers it actually affects. `ExpandCoreAsync` splits: an `Analyzer` row
  calls `ReferenceGraphEngine.RunAsync`, a `Symbol` row materialises branches on the UI thread with no
  fetch. `RefreshExpanded` walks to the shallowest expanded **analyzer** row rather than any expanded row.
- **Verify:** `dotnet build RoslynQuery.slnx -c Debug` succeeds (XAML compiles). Behaviour is covered by
  step 26's smoke test.
- **Commit:** `drop the usage filter and wire the analyzer tree into the tool window`

### 23. Coloured, ILSpy-spelled signature rows `[ ]`

- **Files:** `RoslynQuery/ReferenceGraph/ReferenceGraphDisplay.cs`,
  `RoslynQuery/ToolWindow/SymbolDisplayPartsConverter.cs` (new),
  `RoslynQuery/ToolWindow/ReferenceGraphToolWindowControl.xaml`,
  `RoslynQuery.Tests/ToolWindow/SymbolDisplayPartsConverterTests.cs` (new)
- **Do:** Before writing the converter, **probe how to reach Roslyn's classification brushes from an
  in-proc tool window** - `IClassificationFormatMapService` + `IClassificationTypeRegistryService` off
  `IComponentModel` is the expectation, but confirm which format map to request (`"text"` vs `"tooltip"`)
  and that the brushes track a theme switch. Then: `ReferenceGraphDisplay` switches to
  `NameAndContainingTypesAndNamespaces` and returns `ImmutableArray<SymbolDisplayPart>`, appending a
  hand-built `: ReturnType` run - the probe confirmed no display-format flag does this. The node stores
  the parts alongside the flat string, which stays for tests and for the status line. The converter maps
  `SymbolDisplayPartKind` to classification brushes and yields WPF `Run`s; the row template binds them
  into a `TextBlock`. Falls back to the plain foreground brush when the classification services are
  unavailable, so the window still renders outside a full VS host.
- **Verify:** `dotnet build RoslynQuery.slnx -c Debug` succeeds, then
  `RoslynQuery.Tests.exe -class "RoslynQuery.Tests.SymbolDisplayPartsConverterTests"` passes. The part
  kind to classification name mapping is a pure function and is tested directly; brush resolution needs
  a VS host and is left to the smoke test.
- **Commit:** `render signature rows with syntax colouring`

### 24. Reference Graph options page `[ ]`

- **Files:** `RoslynQuery/Options/ReferenceGraphOptionsPage.cs` (new),
  `RoslynQuery/RoslynQueryPackage.cs`,
  `RoslynQuery.Tests/Options/ReferenceGraphOptionsPageTests.cs` (new)
- **Do:** A `DialogPage` registered with `[ProvideOptionPage]` under a `RoslynQuery` category, named
  `Reference Graph`, carrying two boolean settings: **Enable IL analysis** (would let `Uses` continue
  past the source boundary by decoding the callee's IL body) and **Enable reverse IL analysis** (would
  let `Used By` report framework callers by indexing every method body in every referenced assembly).
  **Both ship disabled and non-functional in this phase** - nothing reads them. Wire them as read-only
  in the UI (greyed, `ReadOnly` / disabled descriptors) so they are visibly forthcoming rather than
  broken. The reverse-IL setting carries a prominent description saying plainly that framework code
  calling into your code is rare, that the option therefore earns its keep only in unusual situations,
  and that it costs a full scan of every referenced assembly.
- **Verify:** `dotnet build RoslynQuery.slnx -c Debug` succeeds, then
  `RoslynQuery.Tests.exe -class "RoslynQuery.Tests.ReferenceGraphOptionsPageTests"` passes. Cover: both
  properties default to false; both are marked read-only/disabled; the descriptions are non-empty (the
  warning text is the point of the reverse-IL one). A `DialogPage` is constructible outside a VS host,
  so this needs no shell.
- **Commit:** `add a reference graph options page with the deferred il analysis switches`

### 25. Navigate metadata rows to decompiled source `[ ]`

- **Files:** `RoslynQuery/RoslynQuery.csproj`,
  `RoslynQuery/Navigation/DecompiledSourceProvider.cs` (new),
  `RoslynQuery/ToolWindow/ReferenceGraphToolWindowControl.xaml.cs`,
  `RoslynQuery.Tests/Navigation/DecompiledSourceProviderTests.cs` (new)
- **Do:** **Start with a load probe and stop if it fails.** Add `ICSharpCode.Decompiler` 11.0.0.9375 to
  the VSIX, deploy to the experimental instance, and confirm it loads and decompiles a member in-proc.
  The probe already hard-failed once outside VS on `System.Memory` unification (fixed by pinning 4.6.3),
  and devenv carries its own `System.Memory`, `System.Collections.Immutable` and
  `System.Reflection.Metadata` under its own redirects, so this is the step's real risk. If it cannot be
  made to load reliably, **mark this step `[!]` and leave metadata rows non-navigable** rather than
  shipping a half-working navigation - the whole point of the feature is seeing implementations, and a
  fallback to the signature-only `[from metadata]` view does not deliver that.
  If it does load: `DecompiledSourceProvider` maps a `SymbolIdentity` to decompiled C# via
  `IdStringProvider.FindEntity` then `CSharpDecompiler.DecompileTypeAsString` for the containing type
  (type context, the way ILSpy shows it), caches one `CSharpDecompiler` per assembly path, writes the
  result to a temp file under the extension's own folder, and opens it read-only, positioning on the
  member. Double-click and Enter route metadata rows here instead of to `SpanMapper`.
- **Verify:** `dotnet build RoslynQuery.slnx -c Debug` succeeds, then
  `RoslynQuery.Tests.exe -class "RoslynQuery.Tests.DecompiledSourceProviderTests"` passes. Cover, against
  mscorlib on disk: a documentation id for `System.IO.MemoryStream.Read` produces source containing a
  real body (assert on a statement, not just a signature); an id that matches nothing returns null
  rather than throwing; the per-assembly decompiler is constructed once across repeated calls. Then the
  in-VS half of the check belongs to step 26's smoke test.
- **Commit:** `navigate metadata rows to decompiled source`

### 26. README and the full smoke test `[ ]`

- **Files:** `README.md`
- **Do:** Rewrite the Reference Graph section for the analyzer model: the branch set per symbol kind,
  that every row re-analyses, the `(N in T ms)` headers, the removal of the usage filter, what the scope
  combo now applies to, how far into referenced assemblies each branch reaches, and the options page
  with its two deferred switches. No new C# fences.
- **Verify:** `dotnet build RoslynQuery.slnx -c Debug`, then
  `RoslynQuery.Tests.exe -class "RoslynQuery.Tests.ReadmeExampleTests"` passes, then the manual F5 smoke
  test **including everything steps 8, 12 and 14 never got walked through**, since this phase rewrites
  the tree they were verifying: both a member and a type root show the right branch set; a branch header
  gains its count and timing on expand; an empty branch loses its expander; a row three levels down
  still offers its own full branch set; `Overrides` / `Implements` / `Derived Types` / `Extension Methods`
  each return something on a solution known to have them; a framework row appears under `Derived Types`
  rooted on a framework type and is marked as metadata; double-click still navigates without toggling
  (step 12); Enter navigates from the selected row (step 14); a metadata row opens **decompiled source
  with real bodies** (step 25); signature colouring survives a Tools > Options theme switch; the options
  page appears with both switches visibly disabled.
  **Re-run this step's verify after step 28**, which adds root kinds this documentation has to cover.
- **Commit:** `document the analyzer model and finish the smoke test`

### Steps added after phase 2 was planned

Steps 27-28 come from a user challenge to the phase-1 non-goal list: a tool calling itself a reference
graph should handle locals, parameters, type parameters, namespaces, lambdas and local functions.
The challenge was correct, and probing showed the blocker was much narrower than the non-goal implied.

**Probe findings (Microsoft.CodeAnalysis 5.6.0, measured):**

| Root kind | `DocumentationCommentId` | `FindReferencesAsync` | `(file, span)` re-resolve |
| --- | --- | --- | --- |
| namespace | `N:Outer.Inner`, resolves | works | works |
| local function | full doc id, resolves | works | works |
| lambda | doc id resolves | **always 0 - nothing can refer to a lambda** | works |
| type parameter | **null** | works (3 hits) | works |
| parameter | **null** | works (2 hits) | works |
| local | **null** | works (5 hits) | works |

So namespaces and local functions were never blocked by anything but `SymbolResolver.IsSupportedRoot`
turning them away, and only locals, parameters and type parameters actually need a new identity.

### 27. Namespace, local function and lambda roots `[ ]`

- **Files:** `RoslynQuery/ReferenceGraph/SymbolResolver.cs`,
  `RoslynQuery/ReferenceGraph/ReferenceAnalyzerKind.cs`,
  `RoslynQuery/ReferenceGraph/ReferenceAnalyzers.cs`,
  `RoslynQuery/ReferenceGraph/ReferenceGraphEngine.cs`,
  `RoslynQuery.Tests/ReferenceGraph/ReferenceAnalyzersTests.cs`,
  `RoslynQuery.Tests/ReferenceGraph/SymbolResolverTests.cs`, `README.md`
- **Do:** These three need no identity work - the existing `SymbolIdentity` already round-trips all of them.
  **Split the root test from the engine's target filter first:** `IsSupportedRoot` currently does double duty
  as "what can root a graph" and, via `ReferenceGraphEngine.Walk`/`Normalize`, as "what deserves a row in an
  outgoing result". Widening it alone would put a namespace row under `Uses` for every qualified name
  (`Outer.Inner.Holder` binds `Outer` and `Inner` to namespace symbols), so add `IsGraphTarget` holding the
  current narrower set for the engine and let `IsSupportedRoot` widen. Then accept
  `MethodKind.LocalFunction`, `MethodKind.AnonymousFunction` and `SymbolKind.Namespace` as roots, add a
  `Contains` analyzer kind, and extend `ReferenceAnalyzers.For`: namespace gets `UsedBy` + `Contains`
  (its member types and sub-namespaces, straight off `GetMembers`); local function gets `Uses` + `UsedBy`;
  lambda gets `Uses` **only**, because the probe measured `Used By` at a permanent zero.
  Accepting local functions as graph targets is a deliberate side effect: a call to one becomes a real row
  under `Uses`, which it never did before.
- **Verify:** `dotnet build RoslynQuery.slnx -c Debug` succeeds, then
  `RoslynQuery.Tests.exe -class "RoslynQuery.Tests.ReferenceAnalyzersTests"` and
  `-class "RoslynQuery.Tests.SymbolResolverTests"` pass. Cover: a caret on a namespace, a local function
  and a lambda each resolve to a root; a namespace root gets `UsedBy` + `Contains` and its `Contains`
  lists the namespace's types; a lambda root gets `Uses` and **not** `UsedBy`; an outgoing walk over a
  method containing a qualified type name produces **no** namespace rows (the `IsGraphTarget` split);
  an outgoing walk over a method that calls a local function **does** produce a row for it.
- **Commit:** `add namespace, local function and lambda roots`

### 28. Positional identity for locals, parameters and type parameters `[ ]`

- **Files:** `RoslynQuery/ReferenceGraph/SymbolIdentity.cs`,
  `RoslynQuery/ReferenceGraph/SymbolResolver.cs`,
  `RoslynQuery/ReferenceGraph/ReferenceAnalyzers.cs`,
  `RoslynQuery.Tests/ReferenceGraph/PositionalIdentityTests.cs` (new), `README.md`
- **Do:** `DocumentationCommentId.CreateDeclarationId` returns null for these three, so `SymbolIdentity`
  gains a second form: a file path plus the declaration's `TextSpan`, resolved by finding the node at that
  span in the current tree and calling `GetDeclaredSymbol`. The probe confirmed this round-trips exactly for
  all three. Keep it a single struct with a discriminator rather than an interface - it is stored on every
  node and compared constantly. A positional identity is only valid while the file is unedited, so
  re-resolution runs the span through `SpanMapper` first, the same way navigation already does, and a row
  whose span no longer resolves reports the existing "no longer exists in the current solution" message.
  Branch sets: local and parameter get `ReadBy` + `AssignedBy` (a parameter is writable, and `ref`/`out`
  arguments are already classified `Write`); type parameter gets `UsedBy`.
- **Verify:** `dotnet build RoslynQuery.slnx -c Debug` succeeds, then
  `RoslynQuery.Tests.exe -class "RoslynQuery.Tests.PositionalIdentityTests"` passes. Cover: a local, a
  parameter and a type parameter each round-trip through `SymbolIdentity` and resolve back to the same
  symbol; two locals of the same name in different methods do not compare equal; a local's `ReadBy` and
  `AssignedBy` split its occurrences correctly; resolution against a solution where the declaration has
  been edited away returns null rather than throwing or resolving to the wrong symbol.
- **Commit:** `identify locals, parameters and type parameters by position`

## Deviations

- **Status field records the commit subject, not the hash.** Rule 5 requires the code change and this
  file's update to land in one commit, so a hash recorded in that same commit can never be its own.
  The field is `Last synced commit subject` instead; check it with `git log -1 --format=%s`.

- **Step 15: branch order is fixed to the screenshots, which the step text did not specify.** Members read
  `Uses`, `Used By`, `Overridden By`, `Overrides`, `Implements` / `Implemented By`, matching the second
  screenshot's root row. Types read `Uses`, `Instantiated By`, `Used By`, `Exposed By`, `Derived Types`,
  `Implemented By`, `Extension Methods`, `Applied To`.
- **Step 15: ILSpy offers neither `Uses` nor `Derived Types` on a type; both are kept anyway.** ILSpy has
  no type-level `Uses` analyzer at all, and surfaces derived types elsewhere in its own tree rather than in
  the Analyzer pane. Phase 1 already answered outgoing references for a type, and `Derived Types` falls out
  of `FindDerivedClassesAsync` for free, so dropping either to match the reference would lose working
  behaviour for no gain.
- **Step 15: `Implements` applicability compares override roots, not symbol identity.** The implementing
  member can sit several levels up the override chain, so `Middle.Area` overriding `Base.Area` (which is
  what actually implements `IShape.Area`) still has to report `Implements`. This is the same walk step 17's
  finder needs, found here first. `OverrideOfAnImplementingMember_StillGetsImplements` covers it.
- **Step 15: interface members report `IsAbstract == true`,** so `Overridden By` cannot be excluded by
  modifiers alone - an interface member would otherwise offer a branch that `FindOverridesAsync` always
  answers empty (see the probe findings). The exclusion keys off the containing type's kind instead.
- **Step 15: a static class does not get `Instantiated By`.** Not called out in the step text; it cannot be
  constructed, so the branch could only ever be empty.
- **Step 17: `Describe` now returns null when no usage kind is present.** A hierarchy row points at a
  declaration rather than a usage, so its single location carries `ReferenceUsageKind.None`; without the
  guard the secondary line rendered as `1 ref ()`. Cost one extra file beyond the step's list
  (`ReferenceGraphNode.cs`).
- **Step 17: `Overrides` keeps chain order; every other hierarchy branch sorts.** Nearest-first is the
  meaningful order for an override chain and is already deterministic, so it needs no sort. The rest sort by
  display text then declaration id, the same tie-break the incoming rows use, because `SymbolFinder` gives no
  ordering guarantee.
- **Step 17: results are de-duplicated on `SymbolIdentity`.** A transitive search can return overlapping
  sets - `FindImplementationsAsync` on an interface walks both the implementing types and their subclasses -
  and two rows for one symbol would each expand into the same subtree.
- **Step 17: a hierarchy row navigates to `DeclaringSyntaxReferences[0].Span`.** A metadata symbol has none,
  so it gets no location and is not navigable yet; steps 21 and 25 own that.
- **Step 17 extra: a test pins Roslyn's own behaviour.**
  `FindImplementedInterfaceMembers_CalledDirectlyOnAnOverride_ReturnsNothing` asserts the empty result that
  forced the override-chain walk, so if a future Roslyn release changes it, the reason for the walk stops
  being invisible.
- **Step 16: the step's file list was incomplete.** Deleting `ReferenceDirection` breaks
  `ReferenceGraphEngine`, `SymbolGlyphMonikerConverter` and `ReferenceGraphToolWindowControl`, so all three
  moved in the same commit. There is no smaller version of this step that still builds.
- **Step 16: `ExpandSymbol` was replaced by deciding the branch set at construction.** The step called for
  a method that materialises a symbol row's branches on expand, but `ReferenceAnalyzers.For` needs a live
  `ISymbol` and a node deliberately keeps none - the same constraint that already forces `SymbolGlyphs.For`
  to run once at construction. The applicable kinds are therefore computed while the symbol is still in
  hand and stored on the row, which makes opening a symbol row free rather than merely synchronous.
- **Step 16: `IsExpandable` is now computed, and means "is an analyzer row".** It was a stored flag meaning
  "seed a placeholder and fetch on open". Only analyzer rows fetch, so the two concepts collapsed into one
  and `ShallowestExpanded`/`ShallowestLoaded` now target analyzer rows without needing a role check of
  their own.
- **Step 16: construction moved behind factories.** `CreateSymbol`, `CreateAnalyzer`, `CreateRoot`,
  `CreateMessage` and `CreateLocation` over a private constructor - the positional parameter list had grown
  past the point where a call site was readable. Note that an object initializer cannot follow a factory
  call, so `IsRecursive` is now assigned after construction in `GroupSet.Build`.
- **Step 16: `ResetToUnloaded` is a no-op on anything but an analyzer row.** Stop previously cleared every
  loaded row; under the new model that would drop a symbol row's branches permanently, since nothing
  refetches them.
- **Step 16: the `Locations (N)` branch is built at construction, not in `SetChildren`.** It depends only
  on the row's own occurrences, and `SetChildren` is now an analyzer-row concern. A recursive symbol row
  still gets its locations branch even though it offers no analyzer branches - the occurrences are real and
  worth reaching.
- **Step 16: the image catalog has no lambda or type-parameter glyph.** Same class of finding as step 6's
  missing constructor glyph, verified by reflecting over Microsoft.VisualStudio.ImageCatalog 17.14:
  `Lambda`, `LocalFunction`, `TypeParameter` and `ClassHierarchy` do not exist. Used `Inline` for a lambda,
  `MethodSnippet` for a local function, `TypeDefinition` for a type parameter and `Hierarchy` for the
  hierarchy branches. `Operator`, `Namespace`, `LocalVariable` and `Parameter` all do exist.
  `Convert_EveryDeclaredGlyph_IsHandled` already asserts every glyph maps to a distinct moniker, so a
  collision here would have failed the build rather than shipping two rows with the same icon.
- **Step 16: analyzer branches share three glyphs, not thirteen.** `Uses` gets `OutgoingBranch`, the
  reference analyzers (`UsedBy`, `ReadBy`, `AssignedBy`, `InstantiatedBy`, `ExposedBy`, `AppliedTo`) get
  `IncomingBranch`, and the structural ones get `HierarchyBranch`. ILSpy's own branch icons are similarly
  undifferentiated.
- **Step 15 correction: an enum member does not get `Assigned By`.** It is an `IFieldSymbol`, so the field
  rule gave it the full field branch set, but an enum member cannot be written - the branch could only ever
  come back empty. Enum members now get `Uses` + `Read By`. Found by widening the step's coverage after the
  step had already been committed; `EnumMember_DoesNotGetAssignedBy` reproduces it.
- **Step 15: non-method symbol kinds were verified rather than assumed.** Operators
  (`MethodKind.UserDefinedOperator`), conversion operators (`MethodKind.Conversion`), indexers, events,
  extension methods, destructors, const and readonly fields, delegates, enums and structs all pass
  `SymbolResolver.IsSupportedRoot` and get a sensible branch set with no special-casing; each now has a test.
  The one thing that did not carry over is the **icon**: an operator renders with the generic method glyph,
  which step 16 fixes.
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
