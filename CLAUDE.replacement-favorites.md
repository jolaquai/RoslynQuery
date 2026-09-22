# Favorites-capable history for replacements

## Context

The Query History sidebar lets a user star, rename and drop compiled predicates, persisted to
`%LocalAppData%\RoslynQuery\favorites.tsv`. The Replace tab has no equivalent: `ReplaceCompiler`
already keeps a process-lifetime cache with a `Snapshot()` method identical to `PredicateCompiler`'s,
but nothing calls it, and there is no store, no sidebar row and no persistence for replacement
expressions.

Rather than add a third hand-copied twin (the compilers are already a copied pair, and the store
would become a second), this change factors the favorites store, the compiler cache and the sidebar
row into shared scaffolds, then instantiates each one twice. Outcome: a replacement history sidebar
section with the same star / rename / drop behaviour as the query one, and one implementation behind
both instead of two.

Decisions already taken:
- Replacement history is a second sidebar section, visible only while the Replace tab is active.
- Its own file `replace-favorites.tsv`, with its own format stamp and its own version chain.
- Double-click restores into the Replace-with box only. No automatic preview generation.
- The compiler caches get factored too.
- A replacement favorite keys on the replacement expression alone, exactly as a query favorite keys
  on the predicate alone. No find+replace pairing.

## 1. Favorites scaffold

New folder `RoslynQuery/Favorites/`, namespace `RoslynQuery.Favorites`. Deletes
`ToolWindow/FavoritesStore.cs` and `ToolWindow/FavoritesFormat.cs`.

- `FavoriteEntry.cs` - the readonly struct lifted out of `FavoritesStore.Entry`, unchanged
  (`Kind`, `Mode`, `Text`, `Name`, value equality, name not part of the key).
- `FavoritesStore.cs` - the current static class turned into a sealed instance class, constructed
  with a file name and an `FavoritesFormat`. Every member moves across verbatim: `All`, `Contains`,
  `NameOf`, `Add`, `Rename`, `Remove`, `TakeWarning`, `Load`, `Save`, `MoveAside`, the lock, the
  temp-file-then-copy write, the refuse-to-write latch.
  Test seam stays a static: `FavoritesStore.DirectoryOverride` keeps its current
  assign-means-reload semantics by having the setter reset every instance (instances register
  themselves in a static list in the constructor; there are only ever two). `TakeWarning` calls
  `Load` first so a reset store cannot hand back a stale warning.
- `FavoritesFormat.cs` - abstract base holding what both files share: `Read` (via
  `Storage/VersionedFile.cs`), `Write`, and the `Valid` pass (non-empty text, one key once).
  Concrete formats supply `Name`, their `Current` version and its row writer.
  Plus `FavoritesRows`, a static helper with the 4-field parse and row-write used by both v1
  parsers, so sharing the row shape does not mean sharing the version chain.
- `QueryFavoritesFormat.cs` - `QueryFavoritesVersion1 : FormatVersion<IReadOnlyList<FavoriteEntry>>`
  plus the format. Stamp stays `roslynquery-favorites` version 1, so existing files keep loading.
- `ReplaceFavoritesFormat.cs` - its own `ReplaceFavoritesVersion1` and format, stamp
  `roslynquery-replace-favorites` version 1. Independent chain: a v2 on one side never touches the
  other.
- `Favorites.cs` - the two instances: `Favorites.Queries` (`favorites.tsv`) and
  `Favorites.Replacements` (`replace-favorites.tsv`).

`Storage/FormatVersion.cs`, `Storage/VersionedFile.cs` and `Storage/TabSeparated.cs` are already
generic and are reused untouched.

## 2. Compiler cache scaffold

New `RoslynQuery/Query/ExpressionCache.cs`: a sealed instance class holding the
`ConcurrentDictionary` + `ConcurrentQueue` + `_totalEmittedBytes` triple and the whole compile body
(directive rejection, key lookup, `CSharpCompilation.Create`, emit, `Assembly.Load`, delegate bind,
cache-and-enqueue). Constructed with the assembly-name prefix, a template builder delegate
(`delegate string TemplateBuilder(TargetKind, PredicateMode, string, out int offset)`), the emitted
class/method names, and a `Func<TargetKind, Type>` delegate-type map. Exposes `Compile` (both
overloads), `KeyFor` (both overloads), `Snapshot`, `CachedExpressionCount`, `TotalEmittedBytes`.

`PredicateCompiler` and `ReplaceCompiler` stay static classes and become thin facades over one
`ExpressionCache` each, forwarding every existing member. Call sites and existing tests are
unaffected. Two instances means per-compiler byte totals and separate caches stay as they are today.

Behaviour preserved deliberately: each compiler keeps its own `DelegateType` map, so
`PredicateCompiler` still throws `ArgumentOutOfRangeException` and `ReplaceCompiler` still throws
`NotSupportedException` with its "switch Target to SyntaxNode or SyntaxToken" message for
`TargetKind.Operation`.

Net new behaviour: `ReplaceCompiler` gains `KeyFor`, which it currently lacks (it computes the key
inline at `Replace/ReplaceCompiler.cs:66-67`). The dropped-row un-hiding below needs it.

## 3. Sidebar scaffold

- `ToolWindow/CachedPredicateItem.cs` -> `ToolWindow/HistoryItem.cs`, class renamed to `HistoryItem`.
  Body unchanged (the `Pretty` / `Display` / `Tooltip` / `Subtitle` formatting, the rename editor
  state) except for an added `Owner` back-reference to its `HistoryList`.
- New `ToolWindow/HistoryList.cs`: owns one `ObservableCollection<HistoryItem>`, one
  `FavoritesStore`, one session-scoped hidden-rows `HashSet`, and the row actions currently inlined
  in the control - `Refresh(snapshot)` (favorites pinned above the cache snapshot, deduped on the
  shared key, the logic now at `QueryToolWindowControl.xaml.cs:401-421`), `ToggleFavorite`,
  `CommitRename`, `Drop`, `Unhide(key)`. No WPF dependency beyond `ObservableCollection`, so it is
  directly unit-testable.
- `QueryToolWindowControl.xaml.cs` holds two `HistoryList` fields instead of
  `_cachedPredicates` + `_hiddenRows`. The four row handlers (`OnFavoriteClick`, `OnRenameClick`,
  `OnRemoveRowClick`, `CommitRename`) each shrink to one call on `item.Owner`, and serve both lists.
  `RefreshCachedPredicates()` becomes one call per list plus the shared warning report, and its two
  existing call sites in the `finally` blocks (lines 523 and 694) stay put.
- Double-click: the existing `OnCachedPredicateDoubleClick` keeps restoring into the Find box and
  running. A new `OnCachedReplacementDoubleClick` sets `_replacementInput.Text = item.Pretty` and
  stops there.
- Un-hide on re-run: `RunCoreAsync` already drops the query key from hidden rows
  (`QueryToolWindowControl.xaml.cs:540`). `GeneratePreview()` gets the mirror call for
  `ReplaceCompiler.KeyFor(target, replacementExpression)`, placed before the `RunAsync` dispatch
  because `GeneratePreviewCoreAsync` starts off the UI thread and hidden rows are UI-thread-only.

## 4. Sidebar XAML

`QueryToolWindowControl.xaml`: `CachedPredicateTemplate` renamed to `HistoryRowTemplate` and reused
by both lists unchanged (the star / pencil / cross buttons and their handlers are list-agnostic now
that the item carries its owner).

The sidebar `Grid` (currently lines 483-500) becomes three rows: the query section (`*`), a
`GridSplitter` (`Auto`), and a named replacement section (`*`) containing its own "Replacement
History" caption and a `CachedReplacements` `ListBox`. The splitter and the replacement section are
collapsed together while the Search tab is active, with the bottom row height zeroed - the same
trick `SetSidebarExpanded` already uses on `SidebarColumn` (lines 375-398), because `Visibility`
alone leaves a star-sized row behind. Driven by a new `SelectionChanged` handler on `MainTabs`.

No new Tools > Options entry: `DefaultShowHistory` governs the whole sidebar.

## 5. Tests

Existing compiler tests should pass untouched, since the facades keep their public shape. Per the
repo convention, anything new that compiles predicates joins
`[Collection(PredicateCompilerCacheCollection.Name)]`.

- New `FavoritesCollection.cs`: `[CollectionDefinition(Name, DisableParallelization = true)]`,
  mirroring the existing `PredicateCompilerCacheCollection.cs`. Favorites tests touch
  process-wide statics (`FavoritesStore.DirectoryOverride` and the two instances), so both favorites
  classes must be serialized against each other - but not against the 11 compiler classes, hence a
  separate definition.
- `FavoritesStoreTests.cs`: joins that collection; call sites become `Favorites.Queries.*`. The
  temp-dir + `DirectoryOverride` reload seam and all 32 assertions stay as they are. Its class-level
  doc comment about "one class on purpose" is replaced by the collection attribute.
- New `ReplaceFavoritesStoreTests.cs`: the round-trip, MRU, rename and quarantine cases against
  `Favorites.Replacements`, plus the two cases only a second store can have - the stamp is
  `roslynquery-replace-favorites\t1` in `replace-favorites.tsv`, and starring in one store leaves
  the other's file and contents alone.
- New `FavoritesFormatTests.cs`: both formats' stamps and row round-trip, including that each
  refuses the other's stamp.
- `CachedPredicateItemTests.cs` -> `HistoryItemTests.cs`: mechanical type rename, no assertion
  changes.
- `SidebarRowActionTests.cs` -> `HistoryListTests.cs`: today it greps production source text for the
  literals `"FavoritesStore.Remove"` and `"FavoritesStore.Rename"` inside the handlers. With the
  logic in `HistoryList` these become real behavioural tests - dropping a starred row unstars it and
  hides it, a re-run unhides it, renaming a starred row persists and an unstarred one does not.
- `ReplaceCompilerTests.cs`: add a fact that `ReplaceCompiler.KeyFor` matches the key `Compile`
  actually stores, mirroring the predicate-side coverage.

## 6. README

`README.md` has no `### Replace` heading: the entire Replace tab documentation sits inside
`### Favorites` (lines 390-430). Add the missing heading at that boundary, then document the
replacement history under it - the Replace-tab-only sidebar section, `replace-favorites.tsv` with
its own stamp, restore-only double-click, and that a replacement favorite stands alone rather than
being tied to a find predicate. Add a TOC entry beside the existing `Query history` / `Favorites`
lines (lines 27-28).

Safe from `ReadmeExampleTests`, which only parses between `## Using it` and `### Keys`.

## Verification

1. `dotnet build RoslynQuery.slnx` (LSP tools are unreliable here, so the build is the check).
2. Rebuild then run the test exe directly:
   `RoslynQuery.Tests\bin\Debug\net472\RoslynQuery.Tests.exe`. Narrow while iterating with
   `-class "RoslynQuery.Tests.ReplaceFavoritesStoreTests"`.
3. Delete `%LocalAppData%\RoslynQuery\replace-favorites.tsv` if present, then F5 into the
   experimental instance and check end to end:
   - Replace tab shows the second sidebar section; the Search tab does not.
   - Generating a preview adds the replacement expression to that section.
   - Star, rename and drop behave as they do on the query side; double-click fills the Replace-with
     box without kicking off a run.
   - Restart VS: starred replacements come back, `replace-favorites.tsv` exists and is stamped
     `roslynquery-replace-favorites 1`.
   - `favorites.tsv` is byte-identical to before and query favorites still load from it.
