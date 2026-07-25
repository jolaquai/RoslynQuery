# RoslynQuery - implementation plan

Tool window that runs a user-written C# predicate over every `SyntaxNode`, `SyntaxToken` or
`IOperation` in a selectable scope, with IntelliSense in the predicate box, and navigates to a
double-clicked match. Roughly "Syntax Visualizer, but you write the `Where`".

Status: v1 implemented and building clean (Debug + Release, zero warnings). Sections marked
DEFERRED are designed but not built.

## 1. Hosting decision

In-proc VSSDK VSIX, `net472`, `Microsoft.NET.Sdk` project.

Not negotiable: the whole feature reads `VisualStudioWorkspace`, which only exists inside devenv.
The out-of-proc VisualStudio.Extensibility model cannot see it, and in-proc hosting of that SDK
pins you to .NET Framework anyway because devenv is a .NET Framework process.

VS 2026 loads unmodified VS 2022 VSIXes: compatibility is evaluated on API version (18.x supports
17.x) using only the lower bound of `InstallationTarget`, so `[17.0, )` covers 2022 and 2026 from
one artifact.

### Package versions and why

| Package | Version | Note |
| --- | --- | --- |
| `Microsoft.VisualStudio.SDK` | 17.14.40265 | `ExcludeAssets="runtime"` |
| `Microsoft.VSSDK.BuildTools` | 18.5.40034 | VSIX container + pkgdef generation |
| `Microsoft.CodeAnalysis.CSharp.Workspaces` | 5.6.0 | `ExcludeAssets="runtime"` |
| `Microsoft.CodeAnalysis.CSharp.Features` | 5.6.0 | `CompletionService` |
| `Microsoft.VisualStudio.LanguageServices` | 4.14.0 | `VisualStudioWorkspace`; no 5.x published |

VS 2026 ships Roslyn assembly version `5.7.0.0` and `devenv.exe.config` redirects
`0.0.0.0-5.7.0.0 -> 5.7.0.0` for `Microsoft.CodeAnalysis`, `Microsoft.CodeAnalysis.CSharp` and
`Microsoft.VisualStudio.LanguageServices`. Compiling against anything at or below 5.7.0.0 is
therefore safe, which is what makes the 4.14.0 / 5.6.0 mix work (NU1608 is suppressed with that
justification in the csproj).

Every Roslyn and SDK reference is `ExcludeAssets="runtime"`. Shipping a second copy of
`Microsoft.CodeAnalysis.*` inside the VSIX would introduce a duplicate assembly identity and break
MEF composition. Verified: the produced VSIX contains only `RoslynQuery.dll` and its pkgdef.

## 2. Layout

```
RoslynQuery.slnx
RoslynQuery/
  RoslynQueryPackage.cs          AsyncPackage, View > Other Windows command
  RoslynQueryPackage.vsct        command placement (KnownMonikers icon, no bitmap asset)
  VSPackage.resx                 MergeWithCTO target for Menus.ctmenu
  Query/
    QueryKinds.cs                TargetKind, ScopeKind
    PredicateTemplate.cs         single source of the wrapper text and its prefix length
    PredicateCompiler.cs         emit + Assembly.Load + delegate bind, cached
    ScopeResolver.cs             active view -> Roslyn Document -> scope units
    QueryEngine.cs               enumeration, parallel scan, batching, cap
    QueryHit.cs                  DocumentId + TextSpan + display strings
  Editor/
    PredicateContentTypes.cs     private content type + per-buffer target kind
    PredicateDocumentFactory.cs  AdhocWorkspace holding the wrapped predicate
    PredicateCompletionSource.cs IAsyncCompletionSource over Roslyn CompletionService
    PredicateClassifier.cs       lexical colorization
    PredicateInput.cs            editor host + manual completion driving, TextBox fallback
  Navigation/
    SpanMapper.cs                replay spans through edits made since the run
    DocumentNavigator.cs         shell open + select
  ToolWindow/
    QueryToolWindow.cs
    QueryToolWindowControl.xaml(.cs)
```

## 3. Predicate compilation

`CSharpScript` was rejected: `ScriptRunner<T>` costs a `Task` allocation and a globals binding per
invocation, which is unaffordable at 10^5-10^6 calls per run.

Instead `PredicateTemplate` wraps the expression in

```csharp
public static bool Match(SyntaxNode n, SemanticModel model, Document doc) { return <expr>; }
```

which is emitted to a `MemoryStream`, loaded with `Assembly.Load(byte[])`, and bound to a
`NodeMatch`/`TokenMatch`/`OperationMatch` delegate. Per-call cost is a plain delegate invoke.

Known leak, accepted: `net472` has no collectible `AssemblyLoadContext`, so each distinct
expression leaks one ~6 KB assembly for the session. The compile cache is keyed on
`(TargetKind, expression)`, so it is one per unique expression, not one per run or keystroke.

Compile errors are surfaced with the column remapped back into the user's expression
(`start - expressionOffset`), not the generated file's coordinates.

Predicate exceptions are caught per item, counted, and the first message is shown. One
`NullReferenceException` on an unbound symbol must not abort a solution-wide run.

## 4. Corpus and scope

| Scope | Source |
| --- | --- |
| Containing member | `SemanticModel.GetEnclosingSymbol` at the caret, walked up past anonymous functions, then `DeclaringSyntaxReferences` |
| Containing type | same, then `ContainingType` |
| Current document | `Solution.GetDocumentIdsWithFilePath` on the active view's moniker |
| Current project | `document.Project.Documents` |
| Solution | all C# projects |

The active view is read through `IVsTextManager.GetActiveView` + `IPersistFileFormat.GetCurFile`
rather than `GetOpenDocumentInCurrentContextWithChanges`, which would drag in
`Microsoft.CodeAnalysis.EditorFeatures.Text` for one extension method.

Enumeration:

- `SyntaxNode`: `DescendantNodesAndSelf()`
- `SyntaxToken`: `DescendantTokens()`
- `IOperation`: walk syntax only until a node owns an operation
  (`DescendantNodesAndSelf(descendIntoChildren: x => model.GetOperation(x) is null)`), then walk
  `ChildOperations` from there. Calling `GetOperation` on every node would re-enter binding for
  subtrees already covered.

VB documents are skipped rather than errored: the template is C#-typed, so a VB node could never
satisfy it.

The `SemanticModel` is only built when the expression mentions `model` (regex `\bmodel\b`) or the
target is `IOperation`. Binding every document is the dominant cost of a wide run and most
syntax-only predicates never need it. Consequence, documented in the README: reaching the model
indirectly (through a helper that does not literally name `model`) yields `null`.

## 5. Live vs button

Live is only offered for member/type/document scope; the checkbox is disabled for project and
solution. Re-binding a whole solution on every keystroke would churn Roslyn's caches for the
entire IDE, not just this window.

Debounce is 400 ms with a `CancellationTokenSource` swapped per run. Scanning is parallel over
documents, bounded by `Environment.ProcessorCount`. Results stream to the UI in batches of 200 via
`Dispatcher.BeginInvoke` at `Background` priority, so batches coalesce behind user input. Result
cap is user-selectable (default 5 000); tripping it cancels the run and flags the result as capped.

## 6. Result identity and span drift

`QueryHit` deliberately stores `DocumentId` + `TextSpan` + precomputed strings and never a
`SyntaxNode` or `IOperation`. A live node roots its whole tree, so a solution-wide result set would
pin every compilation it touched.

`WeakReference` per hit was considered and rejected: red `SyntaxNode`s are transient wrappers that
would be collected almost immediately (near-100% miss rate), `IOperation` is not cached by identity
at all, and neither one is what holds the memory.

Spans recorded at scan time drift as soon as the user edits: one newline inserted above a match and
every coordinate below it is off by one. `SpanMapper` fixes this by replaying the span through
`Document.GetTextChangesAsync(originalDocument)` before converting to a caret position. The run's
`Solution` snapshot is held in a single `WeakReference<Solution>` on the control (one per run, not
per hit, because a `Solution` roots its compilations); if it has been collected the span is used as
recorded, which is only wrong if the user edited since the run.

## 7. IntelliSense in the predicate box

Two routes were evaluated.

**Route A, native VS IntelliSense.** Create a `Workspace` over VS's own MEF container and call
`OnDocumentOpened` with the buffer's `SourceTextContainer`, so Roslyn's editor features bind to it
directly. This is essentially what C# Interactive does. Blocked in practice: `MefV1HostServices`,
the public bridge from VS's MEF v1 `ExportProvider` into `HostServices`, **no longer exists in the
Roslyn shipped with VS 2026** (verified against `Microsoft.CodeAnalysis.Workspaces.dll` 5.7.0.0;
only `MefHostServices` remains). It would also need an elision buffer to hide the wrapper prefix
from the view while keeping it in the document.

**Route B, chosen.** A private content type plus:

- `IAsyncCompletionSource` that wraps the buffer text with `PredicateTemplate`, hands it to an
  `AdhocWorkspace`-backed `Document`, and calls the public
  `CompletionService.GetCompletionsAsync(doc, offset + position, trigger)`. Descriptions come from
  `CompletionService.GetDescriptionAsync`.
- `IClassifier` doing lexical colorization via `SyntaxFactory.ParseTokens`. Semantic colors would
  need a bound compilation per keystroke, which is not worth it for a one-line box.

The view is a plain WPF `IWpfTextView` from `ITextEditorFactoryService`, not an `IVsTextView`
adapter, so VS's command routing never reaches it. This is still far less machinery than hosting an
HWND editor and implementing `IOleCommandTarget` on the tool window pane, but the price is larger
than it first looked: **the view has no keyboard behaviour of its own at all.** Verified against
`Microsoft.VisualStudio.Platform.VSEditor.dll` 18.x: `WpfTextView` is a bare `ContentControl` whose
only input members are `OnMouseDown` / `OnGotKeyboardFocus` / `OnLostKeyboardFocus`. There is no
`OnKeyDown` and no `OnTextInput`, because in a real editor every keystroke, printable characters
included, arrives as a command through the adapter's `IOleCommandTarget` and lands on
`IEditorOperations`. Wiring only the completion keys, as v1 did, left the box unable to accept a
single character, and unhandled arrows fell through to WPF directional navigation and threw focus
back onto whichever combo box was clicked last.

So `PredicateEditorInput` implements the whole map itself against `IEditorOperations`:

- `PreviewTextInput` -> `InsertText`, skipping the control characters that Ctrl and Alt chords
  produce (a bare `Control`/`Alt` chord is dropped; `Ctrl+Alt` is AltGr and carries real text).
- `PreviewKeyDown` -> caret and selection (`MoveToPreviousCharacter`/`MoveToNextWord`/`MoveToHome`/
  `MoveLineUp` ... , `extend` from Shift), `Backspace`/`Delete` and their Ctrl word-wise variants,
  `SelectAll`/`CopySelection`/`CutSelection`/`Paste`, undo and redo.
- Completion keeps priority on the keys it shares: Up/Down/PageUp/PageDown go to
  `IAsyncCompletionSessionOperations` while a session is live and to the caret otherwise, Tab/Enter
  `Commit` + `Dismiss`, Ctrl+Space invokes, Esc dismisses, Enter with no session runs the query.

Two invariants that are easy to lose:

1. Every caret key is marked `Handled` even when its operation is a no-op. An unhandled arrow is
   what re-introduces the focus-escape bug.
2. Undo needs `ITextUndoHistoryRegistry.RegisterHistory(buffer)`. `IEditorOperations` opens
   transactions against the registered history and silently runs untracked without one.

Focus needs help too. The host control is focusable in its own right, so `PreviewMouseDown` on the
host redirects to `_view.VisualElement` unless focus is already inside it, and the control focuses
the box once at `DispatcherPriority.Input` after `Loaded`.

Note for future edits: `IAsyncCompletionSession` has no `CommitAndDismiss` and no selection
methods; those live on `IAsyncCompletionSessionOperations`.

Everything is wrapped so failure degrades instead of breaking: `PredicateInputFactory` falls back
to a plain `TextBox` and reports why, and `PredicateDocumentFactory` returns `null` (no completion)
rather than throwing.

## 8. Navigation

`VsShellUtilities.OpenDocument` + `IVsTextView.SetSelection`/`CenterLines`. Roslyn's own
`IDocumentNavigationService` is internal and not an option. Source-generated documents have no
`FilePath`; they are reported rather than silently ignored.

## 9. Deferred

1. **Signature help.** Roslyn's `SignatureHelpService` is internal. Needs hand-rolled parameter
   tooltips or route A.
2. **Error squiggles in the box.** Currently compile errors only appear on Run, in the status line.
   A tagger over the wrapped document's diagnostics is straightforward.
3. **Semantic classification.** Async tagger with per-snapshot caching, `Classifier.GetClassifiedSpansAsync`.
4. **Better insert text.** Completion items commit their `DisplayText`; Roslyn's `override`/partial
   completions want `GetChangeAsync`.
5. **Details pane for the selected hit** (the syntax/operation subtree). This is where the run's
   `WeakReference<Solution>` earns a second use: re-materialize the node from the snapshot that
   produced it instead of one the user has since edited.
6. **Editor highlight adornment** as the selection moves, like Syntax Visualizer.
7. **Stale-results indicator** driven by `Workspace.WorkspaceChanged`.
8. **VB support.** Needs a second template and `VisualBasicExtensions.Kind`.

## 10. Verification

```
msbuild RoslynQuery/RoslynQuery.csproj -t:Rebuild -p:Configuration=Release -p:DeployExtension=false
```

Clean in Debug and Release. Confirmed in the produced VSIX: only `RoslynQuery.dll` +
`RoslynQuery.pkgdef`, no Roslyn or SDK assemblies; manifest version stamped from `<Version>`;
pkgdef registers the package, `Menus.ctmenu` and the tool window.

Manual smoke test, not yet run: F5 into the experimental instance, View > Other Windows >
Roslyn Query, open a C# file, `n.IsKind(SyntaxKind.IfStatement)` at document scope, double-click a
result.
