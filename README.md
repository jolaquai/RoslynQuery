# Roslyn Query

Two Visual Studio tool windows over Roslyn.

**Roslyn Query** runs a C# predicate you write over every `SyntaxNode`, `SyntaxToken` or
`IOperation` in a scope you pick, jumps to a double-clicked match, and can replace what it finds.
`View > Other Windows > Roslyn Query`.

**Reference Graph** roots a lazily-expandable tree on the member or type at the caret, showing what
references it and what it references, recursively.
`View > Other Windows > Reference Graph`, or right-click in the editor.

## Contents

- [Roslyn Query](#roslyn-query)
  - [Contents](#contents)
  - [Using it](#using-it)
    - [Examples](#examples)
    - [Keys](#keys)
    - [Query history](#query-history)
  - [Replace](#replace)
  - [Reference Graph](#reference-graph)
    - [Scope](#scope)
    - [Usage kinds](#usage-kinds)
  - [Building](#building)

## Using it

The window has two tabs, **Search** and **Replace**, sharing one Find box and one set of
Target/Scope/Cap/Generated settings between them. Search browses and navigates; Replace, described
below, additionally writes matches back.

Pick a **Target**, pick a **Scope**, type a predicate, press Enter (or Run).

The signature line above the box tells you what is in scope:

| Target      | Predicate signature                                                          |
| ----------- | ----------------------------------------------------------------------------- |
| SyntaxNode  | `async ValueTask<object> (SyntaxNode n, SemanticModel model, Document doc)`  |
| SyntaxToken | `async ValueTask<object> (SyntaxToken t, SemanticModel model, Document doc)` |
| IOperation  | `async ValueTask<object> (IOperation op, SemanticModel model, Document doc)` |

`System`, `System.Collections.Generic`, `System.Collections.Immutable`, `System.Linq`,
`System.Text`, `System.Text.RegularExpressions`, `System.Threading.Tasks`,
`Microsoft.CodeAnalysis`, `.CSharp`, `.CSharp.Syntax`, `.Operations` and `.Text` are already
imported.

Despite the `object` return, a predicate is still ordinarily written as a plain `bool`
expression/body - `true` means match, `false` and `null` both mean no match. The one other thing
you can return is the parameter's own type (`SyntaxNode` for a SyntaxNode search, `SyntaxToken` for
a SyntaxToken search, `IOperation` for an IOperation search): that lets a single query pick a
*different* location to report as the hit - a Where+Select in one, e.g. matching an `if` statement
but reporting its containing method:

```csharp
if (n.IsKind(SyntaxKind.IfStatement)) return n.FirstAncestorOrSelf<MethodDeclarationSyntax>();
return false;
```

The returned node/token/operation must actually come from the tree being searched - one of its own
descendants, ancestors, or siblings is fine, but a node built with `SyntaxFactory` (or lifted from
some other document) is rejected as an error for that hit rather than silently accepted, since
neither navigation nor a later Replace pass could resolve it back to anything real.

Write either a single boolean expression or a full statement body ending in a `return` - which one
you meant is detected from the text, so nothing needs switching:

```csharp
n is MethodDeclarationSyntax m && m.ParameterList.Parameters.Count > 3
```

```csharp
var m = n as MethodDeclarationSyntax;
if (m is null) return false;
return m.Body?.Statements.Count > 20;
```

Predicates are compiled `async`, so you can `await` inside one:

```csharp
(await doc.GetSyntaxRootAsync()).DescendantNodes().Count() > 500
```

Awaiting is worth it only for something a predicate cannot get synchronously - it runs once per
node, so an `await` on a hot path costs you across the whole scope.

Scopes: containing member, containing type, current document, current project, solution. The three
narrow ones are resolved from the caret in the last active code window.

### Examples

```csharp
n.IsKind(SyntaxKind.IfStatement)
```

```csharp
n is InvocationExpressionSyntax i && i.ArgumentList.Arguments.Count > 4
```

```csharp
n is MethodDeclarationSyntax m && m.Modifiers.Any(SyntaxKind.AsyncKeyword)
    && !m.Identifier.Text.EndsWith("Async")
```

```csharp
t.IsKind(SyntaxKind.StringLiteralToken) && t.ValueText.Length > 200
```

```csharp
op is IConversionOperation c && c.GetConversion().IsBoxing
```

```csharp
n is IdentifierNameSyntax id && model.GetSymbolInfo(id).Symbol is IMethodSymbol { IsStatic: true }
```

### Keys

| Key                   | Effect                                                                                                                |
| --------------------- | --------------------------------------------------------------------------------------------------------------------- |
| Ctrl+Enter            | run (Search in the Find box, Generate Previews in the Replace box), or commit the completion item if the list is open |
| Enter / Shift+Enter   | newline in the box                                                                                                    |
| Ctrl+Space            | invoke completion                                                                                                     |
| Up / Down             | move through the completion list, otherwise move the caret                                                            |
| Tab                   | commit the completion item, otherwise leave the box                                                                   |
| Esc                   | dismiss the completion list, never leaves the box                                                                     |
| `.` `(` `,` operators | commit the completion item, then type the character                                                                   |
| Double-click a result | open the file and select the match                                                                                    |
| Double-click history  | restore that predicate and re-run it                                                                                  |

The predicate box is a real editor view, not a `TextBox`, and it is not wired into VS's command
routing, so its keyboard map is implemented by the extension. Caret movement, selection, backspace
and delete (with the Ctrl word-wise variants), Ctrl+A/C/X/V and Ctrl+Z/Y all work; anything beyond
that is not bound.

The commit characters are Roslyn's C# set without the space, so `n.IsK` + `(` commits `IsKind` and
gives you `n.IsKind(`, but a space just types a space. Nothing commits while the selection is soft,
which is what the list shows right after a bare Ctrl+Space.

Every run is explicit: Ctrl+Enter or the Run button. There is no run-as-you-type mode, on purpose. Each
distinct expression leaks a small assembly for the session (see below), and a debounced re-run turns
that into one leak per pause in your typing.

**Cap** stops the run once that many matches are found and marks the results as capped.

**Generated** includes generated documents. A document counts as generated if its name matches the
usual conventions (`.g.cs`, `.g.i.cs`, `.designer.cs`, `.generated.cs`, `.AssemblyInfo.cs`,
`.AssemblyAttributes.cs`, `TemporaryGeneratedFile_*`), if it sits anywhere under an `obj` or `bin`
directory, if it opens with an `<auto-generated>` header comment, or if every top-level type in it
carries `[GeneratedCode]`. The last two are the ones that catch an SDK `AssemblyInfo.cs`, which
matches no name convention at all. Source-generated documents are included too, and since they have
no file on disk, double-click reports that instead of navigating.

The filter only applies to project and solution scope. A document you have pointed the caret at is
always scanned, generated or not.

### Query history

The **History** button toggles a resizable sidebar listing every predicate still in the compile
cache, newest first. Double-click one to put it back in the box and run it: the target is restored
with it, the scope is left on whatever you currently have selected.

Entries are shown re-formatted. The cache is keyed on a minified form of the text, so two spellings
of the same predicate are one entry and one compiled assembly.

That cache is also the leak referred to above. Every distinct predicate emits an assembly, and
.NET Framework has no way to unload one, so it stays for the life of the VS process; the status line
reports the running count and total size. The cache is capped at 512 entries, which bounds the list,
not the underlying leak.

## Replace

The **Replace** tab sits next to Search and shares its Find box, Target, Scope and Cap - there is
one query, and Replace runs it itself rather than requiring a prior Search run.

Type a **replacement** below the Find box and press **Generate Previews** (or Ctrl+Enter in the
replacement box, the same way Ctrl+Enter in the Find box runs Search). Generate Previews re-runs the
Find query itself first, so there is no need to switch to Search and run it separately - and
switching tabs to run Search directly clears any previews still on screen, so they never go stale
against a changed query. It returns one of:

| Return                       | Effect                                                    |
| ---------------------------- | --------------------------------------------------------- |
| `string`                     | Replaces the match's text verbatim.                       |
| `SyntaxNode` / `SyntaxToken` | Replaces structurally; normalized and re-printed as text. |
| `null`                       | Skips the match.                                          |

The replacement signature mirrors the predicate's:

| Target      | Replacement signature                                                        |
| ----------- | ---------------------------------------------------------------------------- |
| SyntaxNode  | `async ValueTask<object> (SyntaxNode n, SemanticModel model, Document doc)`  |
| SyntaxToken | `async ValueTask<object> (SyntaxToken t, SemanticModel model, Document doc)` |

Replace has no `IOperation` target: an operation is not part of the syntax tree, so there is nothing
to structurally replace it with. The tab disables itself with an explanation when Target is set to
IOperation.

Each previewed match gets a checkbox, checked by default. A match that can't actually apply - two
overlapping spans, a null result, a span that's gone stale - has its box unchecked and disabled
instead, with a warning explaining why; there's no re-checking your way past it. **All** / **None**
toggle every checkbox that isn't disabled.

**Apply Selected** writes the checked replacements back to the workspace in one pass. Spans are
re-resolved against the live solution at apply time, so an edit made between preview and apply
doesn't silently land in the wrong place; anything that no longer resolves is skipped and reported
rather than applied somewhere wrong. The apply is wrapped in VS's linked undo transaction API, and
every changed document is explicitly enrolled in it before the edit lands, so a change spanning
several files is one Ctrl+Z.

Structural (`SyntaxNode` / `SyntaxToken`) replacements are reformatted against their surroundings at
apply time, so indentation comes out matching the rest of the file even when the replacement text
itself was flush-left or otherwise unindented.

## Reference Graph

A second window, styled after the built-in Call Hierarchy but generalized to every kind of
reference rather than just calls.

Right-click a method, constructor, property, field, event or type in the editor and choose
**View Reference Graph**, or open the window empty from `View > Other Windows > Reference Graph`.

Each invocation adds a root at the top of the list rather than replacing what is there, so the
window keeps a history; **Clear** empties it. Every root has two branches:

| Branch                | What is under it                       |
| --------------------- | -------------------------------------- |
| `References To 'X'`   | The declarations that reference `X`    |
| `References From 'X'` | The declarations `X` itself references |

Every row expands the same way, recursively, staying in the direction its branch started in. A row
is one declaration, not one call site: its second line reads `3 refs (1 invocation, 2 reads)`. A row
backed by more than one occurrence opens onto a `Locations (3)` branch listing each of them, ahead of
the rows the graph continues into.

Double-click a row, or select it and press Enter, to jump to it - the individual location rows go to
that exact occurrence, and a row with only one occurrence goes straight there. A row whose symbol
already appears above it in the tree is marked `(recursive)` and does not expand further, so a cycle
terminates instead of looping.

### Scope

The **scope** combo - current document, current project, my solution - narrows `References To`
only. `References From` is read out of the root's own declarations and never searches outside them,
so there is nothing for a scope to narrow. Scope is measured from the declaration of the row being
expanded, not from wherever the caret happens to be at the time.

### Usage kinds

The **Filter** button opens a checkbox flyout, one box per kind of reference:

| Kind            | What it matches                                                                              |
| --------------- | -------------------------------------------------------------------------------------------- |
| Invocations     | The callee of a call                                                                         |
| Reads           | Anything read, including a method group                                                      |
| Writes          | An assignment target, an `out`/`ref` argument, `++`/`--`, `+=` on events                     |
| Constructions   | `new T(...)`, a `this()`/`base()` initializer, an attribute                                  |
| Type references | A parameter or return type, a base type, a cast, `typeof`, a type argument, a `catch` clause |

Invocations, reads, writes and constructions start on; type references start off, because on a type
root they otherwise swamp everything else. Ticking or unticking a box re-reads every expanded row
immediately, as does **Refresh** and changing the scope. **Stop** cancels whatever is in flight.

A compound assignment is both a read and a write, and is counted under each. Any single branch stops
at 200 rows, with the remainder collapsed into one `N more...` row.

## Building

Requires the Visual Studio SDK workload.

```bash
msbuild RoslynQuery/RoslynQuery.csproj -t:Rebuild -p:Configuration=Release -p:DeployExtension=false
```

The `.vsix` lands in `RoslynQuery/bin/Release/net472/`. F5 on the project launches the experimental
instance with the extension deployed.

Targets VS 2022 and VS 2026 from one artifact.

Tests are xUnit.v3 on Microsoft.Testing.Platform, which builds a self-contained runner:

```bash
dotnet test RoslynQuery.Tests/RoslynQuery.Tests.csproj -c Release
```
