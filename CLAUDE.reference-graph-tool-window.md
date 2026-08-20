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

- **State:** not-started
- **Current step:** 1 - Reference-kind classification model
- **Branch:** v0.3.0
- **Base commit:** e1c9fd34b4185a1f071a2fc0c9da3e0f51643a15
- **Last synced commit:** (none yet - this plan file's own initial commit)
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
- **Symbol identity across expansions:** `ReferenceGraphNode` stores a `Microsoft.CodeAnalysis.SymbolKey` (serializable, survives compilation snapshots), not a live `ISymbol` - re-resolved against `_workspace.CurrentSolution` only when a node is expanded. Rejected: holding the `ISymbol` directly, which would pin the compilation that produced it alive for as long as the tool window stays open (the same problem `QueryHit` already deliberately avoids - see `RoslynQuery/Query/QueryHit.cs:10-13`).
- **Multi-root history:** every invocation (context menu or the window's own toolbar) prepends a new root node to the tree instead of replacing the current one - mirrors VS's real Call Hierarchy behavior. The trash-icon button clears the whole root list. Rejected: single-root replace-on-invoke (loses history; the screenshot's trash icon implies a list worth clearing).
- **Scope combo (Current Document / Current Project / My Solution) only affects the "References To" (incoming) branch.** "References From" (outgoing) is inherently local to the root's own declaration and never searches outside it - document this as a tooltip on the combo, not as a separate disabled state.
- **Cancellation** uses one shared `CancellationTokenSource` field, same idiom as `QueryToolWindowControl._cancellation` (`RoslynQuery/ToolWindow/QueryToolWindowControl.xaml.cs:45`) - not per-node-expansion cancellation tokens. Simplicity over a rare concurrent-expansion benefit.
- **`SymbolKey`'s exact overload set must be empirically verified** (per this project's API-verification convention in the user's global CLAUDE.md) with a throwaway probe program before step 3 is written, since the shape has shifted slightly across Roslyn versions. Do not commit the probe.

## Key files

- `RoslynQuery/Query/QueryEngine.cs` - tree-walking pattern to copy for `FindOutgoingAsync`'s body walk (`ScanNodesAsync`, `RoslynQuery/Query/QueryEngine.cs:180`).
- `RoslynQuery/Query/ScopeResolver.cs` - `GetActiveContext` (caret file/line/column, reuse as-is, line 49), `ResolveDeclarationAsync`'s enclosing-symbol walk to copy for incoming grouping (line 131), `IsDeclarationSymbol` (line 154) as the model for which symbol kinds count as declarations.
- `RoslynQuery/Query/QueryHit.cs` - "hold no live Roslyn object" discipline to follow for `ReferenceGraphNode` (lines 10-13).
- `RoslynQuery/ToolWindow/QueryToolWindowControl.xaml` / `.xaml.cs` - WPF styling conventions, toolbar layout, threading pattern (`Run`/`RunCoreAsync`, lines 231-347), navigation on double-click (`OnResultDoubleClick`, lines 148-170), error reporting (`SetError`, lines 225-229) - the template for the new control.
- `RoslynQuery/ToolWindow/QueryToolWindow.cs`, `RoslynQuery/ToolWindow/TargetMonikerConverter.cs` - templates for `ReferenceGraphToolWindow.cs` and `SymbolKindMonikerConverter.cs`.
- `RoslynQuery/Navigation/DocumentNavigator.cs`, `RoslynQuery/Navigation/SpanMapper.cs` - reused unchanged for navigation.
- `RoslynQuery/RoslynQueryPackage.cs`, `RoslynQuery/RoslynQueryPackage.vsct` - existing command/tool-window registration to extend.
- `RoslynQuery.Tests/PredicateAwaitTests.cs:21-41` - `AdhocWorkspace` + `ProjectInfo.Create` + `workspace.AddDocument` fixture pattern to copy for every new test file.
- `RoslynQuery/RoslynQuery.csproj` - already has `InternalsVisibleTo` for the test project; no new wiring needed for `internal` types.
- `RoslynQuery.slnx` - solution file (not `.sln`).

## Steps

### 1. Reference-kind classification model `[ ]`

- **Files:** `RoslynQuery/ReferenceGraph/ReferenceUsageKind.cs` (new), `RoslynQuery/ReferenceGraph/ReferenceUsageClassifier.cs` (new), `RoslynQuery.Tests/ReferenceUsageClassifierTests.cs` (new)
- **Do:** Add `[Flags] internal enum ReferenceUsageKind { Invocation = 1, Read = 2, Write = 4, Construction = 8, TypeReference = 16 }`. Add `internal static class ReferenceUsageClassifier` with `Classify(SyntaxNode occurrence, ISymbol target) -> ReferenceUsageKind`: inspect `occurrence`'s ancestor syntax - callee of `InvocationExpressionSyntax` -> `Invocation`; LHS of `AssignmentExpressionSyntax`, `ref`/`out` argument, or operand of `++`/`--` -> `Write`; inside `ObjectCreationExpressionSyntax`/`ConstructorInitializerSyntax` -> `Construction`; inside `TypeSyntax`/`BaseListSyntax`/`CastExpressionSyntax`/`TypeOfExpressionSyntax`/`CatchClauseSyntax`/type-argument list -> `TypeReference`; otherwise `Read`. A single occurrence can return combined flags only where genuinely ambiguous (e.g. compound assignment `x += 1` on a field is both `Read` and `Write`) - default to the single most specific flag elsewhere.
- **Verify:** `dotnet build RoslynQuery.slnx -c Debug` succeeds, then `RoslynQuery.Tests/bin/Debug/net472/RoslynQuery.Tests.exe -class "RoslynQuery.Tests.ReferenceUsageClassifierTests"` passes. Cover: plain invocation, field read, field write (assignment LHS), compound assignment (Read|Write), `new Foo()` construction, `this()`/`base()` initializer, parameter type reference, cast, `typeof`, generic type argument, catch clause type.
- **Commit:** `add reference usage kind and classifier`

### 2. Caret symbol resolution `[ ]`

- **Files:** `RoslynQuery/ReferenceGraph/SymbolResolver.cs` (new), `RoslynQuery.Tests/SymbolResolverTests.cs` (new)
- **Do:** `internal static class SymbolResolver` with `ResolveAtCaretAsync(Solution solution, ActiveContext active, CancellationToken)`. Find the document the same way `ScopeResolver`'s private `FindDocument` does (`RoslynQuery/Query/ScopeResolver.cs:171`) - either call it if accessible or duplicate the two-line lookup (it's `private static`, so duplicate rather than change its visibility). Get the semantic model and syntax root, find the token at the caret position (reuse `ScopeResolver`'s `ToPosition` logic at line 179, likely duplicated for the same visibility reason), try `GetDeclaredSymbol` on the token's parent first (caret on a declaration), fall back to `GetSymbolInfo` (caret on a usage), fall back further by walking `token.Parent.Parent` a few levels if the immediate node binds to nothing useful. Restrict the accepted result to `SymbolKind` in {Method, Property, Field, Event, NamedType} (constructors are `IMethodSymbol` with `MethodKind.Constructor`, already covered by `Method`).
- **Verify:** `dotnet build RoslynQuery.slnx -c Debug` succeeds, then `RoslynQuery.Tests/bin/Debug/net472/RoslynQuery.Tests.exe -class "RoslynQuery.Tests.SymbolResolverTests"` passes. Cover: caret on a method declaration's name, caret on a call site, caret on a field declaration, caret inside a method body with the caret actually over an unrelated local (should resolve to the containing method via the declared-symbol path only if caret truly lands on a declaration token - otherwise confirm it resolves to whatever symbol the token under the caret actually binds to, not a silent fallback to "enclosing method").
- **Commit:** `add caret symbol resolver`

### 3. Reference graph node model `[ ]`

- **Files:** `RoslynQuery/ReferenceGraph/ReferenceGraphNode.cs` (new), `RoslynQuery/ReferenceGraph/ReferenceDirection.cs` (new, `internal enum ReferenceDirection { Incoming, Outgoing }`), `RoslynQuery.Tests/ReferenceGraphNodeTests.cs` (new)
- **Do:** Before writing this file, empirically verify `Microsoft.CodeAnalysis.SymbolKey`'s exact API (static `Create`, instance `Resolve`, `GetSymbolKey` extension availability) against the Roslyn package version this project references, via a throwaway console probe - do not commit the probe. Then add `internal sealed class ReferenceGraphNode : INotifyPropertyChanged` with: `DisplayText`, `SecondaryText`, `SymbolKindForGlyph` (or similar, feeds `SymbolKindMonikerConverter` later), `DocumentId` + primary `TextSpan` (first location), `IReadOnlyList<(DocumentId DocumentId, TextSpan Span, ReferenceUsageKind Kind)> Locations`, `ReferenceDirection Direction`, `ReferenceGraphNode Parent` (for ancestor-chain cycle checks in step 5), `bool IsRecursive`, a stored `SymbolKey` string/struct for re-resolution, and a lazily-populated `ObservableCollection<ReferenceGraphNode> Children` seeded with a single placeholder node so the tree shows an expand arrow before the real fetch. Add a helper `bool HasAncestor(SymbolKey key)` walking `Parent` up.
- **Verify:** `dotnet build RoslynQuery.slnx -c Debug` succeeds, then `RoslynQuery.Tests/bin/Debug/net472/RoslynQuery.Tests.exe -class "RoslynQuery.Tests.ReferenceGraphNodeTests"` passes. Cover: constructing a node and resolving its stored `SymbolKey` back to the original `ISymbol` against the same solution's compilation round-trips correctly; `HasAncestor` finds a symbol two levels up the `Parent` chain and correctly returns false for an unrelated symbol.
- **Commit:** `add reference graph node model`

### 4. Engine: incoming references `[ ]`

- **Files:** `RoslynQuery/ReferenceGraph/ReferenceGraphEngine.cs` (new), `RoslynQuery.Tests/ReferenceGraphEngineIncomingTests.cs` (new)
- **Do:** `internal static class ReferenceGraphEngine` with `FindIncomingAsync(ISymbol target, Solution solution, IImmutableSet<Document> documents, ReferenceUsageKind filter, ReferenceGraphNode parent, CancellationToken)`. Call `SymbolFinder.FindReferencesAsync(target, solution, documents, cancellationToken)`. For every `ReferenceLocation` where `!IsCandidateLocation`, classify it with `ReferenceUsageClassifier.Classify`, skip if the result doesn't intersect `filter`, then find the enclosing declaration symbol via `SemanticModel.GetEnclosingSymbol` at that location - same walk as `ScopeResolver.ResolveDeclarationAsync` (`RoslynQuery/Query/ScopeResolver.cs:131`), stopping at the first symbol satisfying the same "declaration symbol" test as `ScopeResolver.IsDeclarationSymbol` (line 154). Group by that enclosing symbol into one `ReferenceGraphNode` per group (respecting `parent.HasAncestor` to mark `IsRecursive` and skip descending further into an already-visited ancestor), each carrying its group's `Locations` list. Cap at 200 nodes with a trailing "N more..." placeholder node.
- **Verify:** `dotnet build RoslynQuery.slnx -c Debug` succeeds, then `RoslynQuery.Tests/bin/Debug/net472/RoslynQuery.Tests.exe -class "RoslynQuery.Tests.ReferenceGraphEngineIncomingTests"` passes. Cover (multi-document `AdhocWorkspace` fixtures per `PredicateAwaitTests.cs:21-41`): a method called from two different methods produces two nodes both flagged `Invocation`; a field read in one method and written in another produces nodes flagged `Read` and `Write` respectively; restricting `documents` to a single document excludes a same-project caller in a different file that passing `null` (whole solution) would include; a type root's incoming set includes both a `new Foo()` site (`Construction`) and a parameter-typed-as-`Foo` site (`TypeReference`) when the filter includes both kinds, and excludes the `TypeReference` one when the filter doesn't.
- **Commit:** `add reference graph engine incoming path`

### 5. Engine: outgoing references `[ ]`

- **Files:** `RoslynQuery/ReferenceGraph/ReferenceGraphEngine.cs` (extend), `RoslynQuery.Tests/ReferenceGraphEngineOutgoingTests.cs` (new)
- **Do:** Add `FindOutgoingAsync(ISymbol root, Solution solution, ReferenceUsageKind filter, ReferenceGraphNode parent, CancellationToken)`. For a member root: union `root.DeclaringSyntaxReferences` (partial methods/types), get each `SemanticModel`, walk descendant nodes (include constructor initializers and property accessor bodies) the way `QueryEngine.ScanNodesAsync` walks a tree (`RoslynQuery/Query/QueryEngine.cs:180`), call `GetSymbolInfo` on each candidate node, classify with the same `ReferenceUsageClassifier.Classify`, skip anything outside `filter`, group by target symbol into one `ReferenceGraphNode` per group (same `HasAncestor`/`IsRecursive`/200-cap handling as step 4). For a type root: union this same walk over every member's declaring syntax plus the type's own `BaseListSyntax` (classified `TypeReference`), since a type has no single body of its own.
- **Verify:** `dotnet build RoslynQuery.slnx -c Debug` succeeds, then `RoslynQuery.Tests/bin/Debug/net472/RoslynQuery.Tests.exe -class "RoslynQuery.Tests.ReferenceGraphEngineOutgoingTests"` passes. Cover: a method that calls two other methods produces two `Invocation` nodes; a method that reads and writes two different fields produces correctly-flagged nodes; a directly self-recursive method's outgoing set marks the self-entry `IsRecursive` and does not attempt to expand it further; a partial method's outgoing set unions references from both partial declarations; a type root's outgoing set includes a reference made only inside one of its members plus its base type.
- **Commit:** `add reference graph engine outgoing path`

### 6. Symbol glyph converter `[ ]`

- **Files:** `RoslynQuery/ToolWindow/SymbolKindMonikerConverter.cs` (new), `RoslynQuery.Tests/SymbolKindMonikerConverterTests.cs` (new)
- **Do:** `IValueConverter` mapping `SymbolKind`/`MethodKind` (method vs constructor vs property vs field vs event vs named type, further split class/struct/interface/enum via `TypeKind` where the input is a `NamedType`) to `Microsoft.VisualStudio.Imaging.Interop.ImageMoniker` values from `KnownMonikers` (Method, Field, Property, Event, Class, Struct, Interface, Enumeration, EnumerationItem as needed) - same shape as `RoslynQuery/ToolWindow/TargetMonikerConverter.cs`.
- **Verify:** `dotnet build RoslynQuery.slnx -c Debug` succeeds, then `RoslynQuery.Tests/bin/Debug/net472/RoslynQuery.Tests.exe -class "RoslynQuery.Tests.SymbolKindMonikerConverterTests"` passes. `Convert` is a pure function over enum inputs - test it directly without any WPF/UI host.
- **Commit:** `add symbol kind moniker converter`

### 7. Tool window UI `[ ]`

- **Files:** `RoslynQuery/ToolWindow/ReferenceGraphToolWindow.cs` (new), `RoslynQuery/ToolWindow/ReferenceGraphToolWindowControl.xaml` (new), `RoslynQuery/ToolWindow/ReferenceGraphToolWindowControl.xaml.cs` (new)
- **Do:** `ReferenceGraphToolWindow` mirrors `QueryToolWindow.cs` (new GUID, `Caption = "Reference Graph"`). `ReferenceGraphToolWindowControl.xaml` mirrors `QueryToolWindowControl.xaml`'s theme brushes and `ThemedDialog*StyleKey` styles. Toolbar: scope `ComboBox` (Current Document / Current Project / My Solution, default Current Project, tooltip noting it only affects "References To"), Refresh button (re-expands every currently-expanded node), Stop button (cancels the shared `CancellationTokenSource`, same field idiom as `QueryToolWindowControl._cancellation`), Clear/trash button (empties the root `ObservableCollection<ReferenceGraphNode>`), and a "Filter" `ToggleButton` opening a `Popup` (`StaysOpen="True"`, `IsOpen` bound to the toggle's `IsChecked`) containing one `CheckBox` per `ReferenceUsageKind` flag - changing any checkbox re-expands every currently-expanded node via the same refresh path as the Refresh button. Body: a `TreeView` bound to the root collection with a `HierarchicalDataTemplate` over `Children`; a node's `Expanded` event (or a `IsExpanded` property setter) triggers the lazy `ReferenceGraphEngine.FindIncomingAsync`/`FindOutgoingAsync` call on a background thread (`ThreadHelper.JoinableTaskFactory.RunAsync(...).FileAndForget(...)` -> `TaskScheduler.Default` -> `Dispatcher.BeginInvoke(DispatcherPriority.Background, ...)` to swap the placeholder child for real results, same shape as `QueryToolWindowControl.Run`/`RunCoreAsync`, lines 231-347). Double-click a node navigates via `SpanMapper.ResolveAsync` + `DocumentNavigator.Navigate`, same as `OnResultDoubleClick` (lines 148-170). Each "root" invocation (a public method the package command handlers will call) resolves the target `ISymbol` (via `SymbolResolver`), builds a new root `ReferenceGraphNode` with two synthetic children ("References To 'X'", "References From 'X'"), and prepends it to the root collection.
- **Verify:** `dotnet build RoslynQuery.slnx -c Debug` succeeds (XAML compiles, no runtime UI test at this step - deferred to step 8's manual smoke test).
- **Commit:** `add reference graph tool window ui`

### 8. Commands, package wiring, and smoke test `[ ]`

- **Files:** `RoslynQuery/RoslynQueryPackage.vsct` (extend), `RoslynQuery/RoslynQueryPackage.cs` (extend)
- **Do:** In the `.vsct`, add a `<Button>` under `IDG_VS_WNDO_OTRWNDWS1` for "Reference Graph" (View > Other Windows), mirroring the existing `cmdidShowQueryToolWindow` button (`RoslynQuery/RoslynQueryPackage.vsct:11`), plus a `View Reference Graph` button in the editor's code-window context menu group (check `vsshlids.h`/`stdidcmd.h` for the exact `IDG_VS_CTXT_CODEWIN_*` group real "Go To Definition" lives in, and use that). In `RoslynQueryPackage.cs`, add `[ProvideToolWindow(typeof(ReferenceGraphToolWindow), Style = VsDockStyle.Tabbed, Window = ...)]` alongside the existing attribute (line 20), wire the "open blank window" command the same way `ShowToolWindowCommandId` is wired (lines 33-41), and wire the context-menu command with a synchronous, cheap `BeforeQueryStatus` (enabled whenever `ScopeResolver.GetActiveContext` finds an active C# view - do not attempt semantic symbol resolution on the UI thread) whose invoke handler resolves the caret symbol (`SymbolResolver.ResolveAtCaretAsync`) off the UI thread and, on success, shows the tool window and roots a new graph on it; on failure (no resolvable symbol), show the tool window with an error line via the same `SetError` pattern `QueryToolWindowControl` already uses (`RoslynQuery/ToolWindow/QueryToolWindowControl.xaml.cs:225-229`).
- **Verify:** `dotnet build RoslynQuery.slnx -c Debug` succeeds, then manually launch the VS experimental instance (F5 on the `RoslynQuery` project) and: right-click a method with known callers/callees in a test solution -> View Reference Graph; confirm both "References To" and "References From" populate; expand a few levels including into a recursive method and confirm it terminates cleanly with an `IsRecursive` marker instead of looping; toggle the filter flyout's `TypeReference` checkbox and confirm the tree refreshes to include/exclude type-usage nodes; double-click a node in each direction and confirm navigation lands on the correct line; open the window from View > Other Windows with no prior invocation and confirm it opens blank without error.
- **Commit:** `wire up reference graph commands and tool window registration`

### 9. README documentation `[ ]`

- **Files:** `README.md` (extend)
- **Do:** Add a "Reference Graph" section mirroring the structure of the existing "Roslyn Query" section: how to open it (View > Other Windows, or right-click a member/type -> View Reference Graph), what the two branches mean, the scope combo's incoming-only scope, and the usage-kind filter flyout. No C# code fences in the new section (or if any are added, they must still satisfy `RoslynQuery.Tests/ReadmeExampleTests.cs`, which compiles every README code fence as a test).
- **Verify:** `dotnet build RoslynQuery.slnx -c Debug` succeeds, then `RoslynQuery.Tests/bin/Debug/net472/RoslynQuery.Tests.exe -class "RoslynQuery.Tests.ReadmeExampleTests"` still passes.
- **Commit:** `document reference graph window in readme`

## Deviations

(none yet)

## Open questions

- Exact `IDG_VS_CTXT_CODEWIN_*` group ID for the editor context-menu command (step 8) - resolve by inspecting `vsshlids.h`/`stdidcmd.h` at implementation time; not blocking earlier steps.
- Exact `SymbolKey.Create`/`.Resolve` overload signatures (step 3) - resolve via the empirical probe described in that step; not blocking steps 1-2.
