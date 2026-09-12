# Roslyn Query

Two Visual Studio tool windows over Roslyn.

**Roslyn Query** runs a C# predicate you write over every `SyntaxNode`, `SyntaxToken` or
`IOperation` in a scope you pick, jumps to a double-clicked match, and can replace what it finds.
`View > Other Windows > Roslyn Query`.

**Reference Graph** analyzes the symbol at the caret the way ILSpy's Analyze pane does: a tree of what
uses it, what it uses, what overrides, implements and derives from it, and more, where every result can
be analyzed in turn.
`View > Other Windows > Reference Graph`, or right-click in the editor.

## Contents

- [Roslyn Query](#roslyn-query)
  - [Contents](#contents)
  - [Using it](#using-it)
    - [Examples](#examples)
      - [SyntaxNode, syntax only](#syntaxnode-syntax-only)
      - [SyntaxNode, with the semantic model](#syntaxnode-with-the-semantic-model)
      - [SyntaxNode, statement bodies](#syntaxnode-statement-bodies)
      - [SyntaxToken](#syntaxtoken)
      - [IOperation](#ioperation)
      - [Using `doc` and `await`](#using-doc-and-await)
    - [Keys](#keys)
    - [Query history](#query-history)
    - [Favorites](#favorites)
  - [Reference Graph](#reference-graph)
    - [Branches](#branches)
    - [Symbols from referenced assemblies](#symbols-from-referenced-assemblies)
    - [Scope](#scope)
    - [Settings](#settings)
  - [Building](#building)

## Using it

> [!TIP]
> These queries are plain Roslyn, so anything you learn about Roslyn applies here directly.
> - [Get started with syntax analysis](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/get-started/syntax-analysis) - nodes, tokens and trivia, which is what a `SyntaxNode` or `SyntaxToken` query matches on.
> - [Get started with semantic analysis](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/get-started/semantic-analysis) - symbols and binding, which is what the `model` parameter gives you.
> - [Syntax Visualizer](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/syntax-visualizer) - `View > Other Windows > Syntax Visualizer`. Put the caret on code you want to match and it names the node type and `SyntaxKind` to write.
> - [SharpLab](https://sharplab.io) - paste code, pick "Syntax Tree" from the results menu, and read the tree without leaving the browser.
> - [SyntaxKind](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.csharp.syntaxkind) - every kind `IsKind` can test for.
> - [dotnet/roslyn](https://github.com/dotnet/roslyn) - the source, when a doc page does not answer it.
>
> The fastest loop is: put the caret on an example of what you are hunting, read the node type out of the
> Syntax Visualizer, then write the `is`-pattern for it.
>
> For concrete examples (which the tests project in this repo compile-check), refer to the [Examples](#examples) below.

The window has two tabs, **Search** and **Replace**, sharing one Find box and one set of
Target/Scope/Cap/Generated settings between them. Search browses and navigates; Replace, described
below, additionally writes matches back.

Pick a **Target**, pick a **Scope**, type a predicate, press Enter (or Run).

The signature line above the box tells you what is in scope:

| Target      | Predicate signature                                                          |
| ----------- | ---------------------------------------------------------------------------- |
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

Different matches that end up reporting the same location - three statements in one method that
all report that method, say - collapse to a single result rather than repeating it once per match.

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

Each of these is a complete predicate: paste one into the Find box, set Target to match, and run.

#### SyntaxNode, syntax only

The cheapest kind of query. `IsKind` tests a node's `SyntaxKind` directly, which is the one-liner form of
"what did the Syntax Visualizer call this":

```csharp
n.IsKind(SyntaxKind.IfStatement)
```

An `is`-pattern both tests the node type and gives you a typed variable to keep querying through. This one
finds calls with more arguments than anyone wants to read at a call site:

```csharp
n is InvocationExpressionSyntax i && i.ArgumentList.Arguments.Count > 4
```

Naming conventions are syntax, so they need no semantic model. An `async` method whose name does not end in
`Async`:

```csharp
n is MethodDeclarationSyntax m && m.Modifiers.Any(SyntaxKind.AsyncKeyword)
    && !m.Identifier.Text.EndsWith("Async")
```

Extended property patterns reach through several properties at once, which keeps a shape test readable.
An empty `catch` block, the classic swallowed exception:

```csharp
n is CatchClauseSyntax { Block.Statements.Count: 0 }
```

A single-statement `if` with no braces, the shape that makes a later edit land outside the `if`:

```csharp
n is IfStatementSyntax { Else: null, Statement: not BlockSyntax }
```

Counting members finds types that have outgrown their file:

```csharp
n is ClassDeclarationSyntax c && c.Members.OfType<MethodDeclarationSyntax>().Count() > 20
```

Trivia hangs off nodes as well as tokens, so a `TODO` anywhere in a file is one query:

```csharp
n.GetLeadingTrivia().Any(tr => tr.ToString().Contains("TODO"))
```

#### SyntaxNode, with the semantic model

`model` answers what a name actually binds to, which syntax alone cannot. Every call to a static method,
regardless of how it was spelled:

```csharp
n is IdentifierNameSyntax id && model.GetSymbolInfo(id).Symbol is IMethodSymbol { IsStatic: true }
```

Symbols carry attributes, so deprecated usage is findable without grepping for names:

```csharp
n is IdentifierNameSyntax dep
    && model.GetSymbolInfo(dep).Symbol is ISymbol s
    && s.GetAttributes().Any(a => a.AttributeClass?.Name == "ObsoleteAttribute")
```

`GetTypeInfo` gives the type of an expression, which is how you find conversions the compiler inserts for
you. A `struct` being boxed into `object`:

```csharp
n is ExpressionSyntax ex
    && model.GetTypeInfo(ex) is { Type.IsValueType: true, ConvertedType.SpecialType: SpecialType.System_Object }
```

Resolving the declaring symbol lets you filter on accessibility, which is not in the syntax at all once
defaults are involved:

```csharp
n is PropertyDeclarationSyntax prop
    && model.GetDeclaredSymbol(prop) is { DeclaredAccessibility: Accessibility.Public, SetMethod: not null }
```

#### SyntaxNode, statement bodies

Anything that needs a local, a loop or an early return is written as a body ending in `return`. Mode is
detected from the text, so there is nothing to switch:

```csharp
var m = n as MethodDeclarationSyntax;
if (m is null) return false;
return m.Body?.Statements.Count > 20;
```

A body is also the readable way to write a multi-step semantic query:

```csharp
if (n is not InvocationExpressionSyntax call) return false;
if (model.GetSymbolInfo(call).Symbol is not IMethodSymbol method) return false;
return method.Name == "ToString" && method.Parameters.Length == 0;
```

Returning a node instead of `true` reports a different location as the hit. Here the match is the `await`,
but the result is the method containing it, so the list is one row per method rather than per `await`:

```csharp
if (!n.IsKind(SyntaxKind.AwaitExpression)) return false;
return n.FirstAncestorOrSelf<MethodDeclarationSyntax>();
```

#### SyntaxToken

Tokens are the level below nodes: keywords, identifiers, literals, punctuation. Long string literals:

```csharp
t.IsKind(SyntaxKind.StringLiteralToken) && t.ValueText.Length > 200
```

Identifiers too short to mean anything:

```csharp
t.IsKind(SyntaxKind.IdentifierToken) && t.ValueText.Length == 1
```

Tokens are where trivia actually lives, so commented-out code is a token query:

```csharp
t.LeadingTrivia.Any(tr => tr.IsKind(SyntaxKind.SingleLineCommentTrivia))
```

#### IOperation

Operations are the semantic tree: the compiler's view after binding, where implicit conversions and
defaulted arguments are explicit. Boxing, as the compiler sees it:

```csharp
op is IConversionOperation c && c.GetConversion().IsBoxing
```

Extension method calls, which look like instance calls in syntax and only separate here:

```csharp
op is IInvocationOperation { TargetMethod.IsExtensionMethod: true }
```

Arguments you did not write, which is how a defaulted parameter shows up:

```csharp
op is IArgumentOperation { ArgumentKind: ArgumentKind.DefaultValue }
```

#### Using `doc` and `await`

`doc` is the whole document, and predicates are compiled `async`, so anything the document can answer is
available. Files big enough to be worth splitting:

```csharp
(await doc.GetSyntaxRootAsync()).DescendantNodes().Count() > 500
```

Remember this runs once per node in scope, so prefer a syntax test first and reach for `doc` only when
nothing cheaper will do.

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
reports the running count and total size.

Nothing is ever evicted from it, deliberately. Dropping an entry cannot reclaim the assembly it
points at, so an eviction only guarantees that re-running that predicate emits and leaks a *second*
assembly for text that already has one. A cap there would accelerate the leak it looks like it
bounds, and it would do it worst to the queries you return to most.

That is also why the row's own drop button only hides the row. The compiled delegate stays cached, so
pasting the same predicate back in later costs nothing and leaks nothing. A dropped row is hidden rather
than forgotten: running that query again brings its row back, and so does the next Visual Studio session,
since the cache behind it is per-process anyway.

### Favorites

Hovering a sidebar row reveals three buttons: a star, a pencil and a cross.

The **star** writes the row to `%LocalAppData%\RoslynQuery\favorites.tsv`, and favorites are listed
above the rest of the sidebar on every load, whether or not the compile cache still holds them. That is
a text-only record: a restored favorite compiles on its first run of a session like any other predicate.

The file's first line stamps the format version, and a reader upgrades an older file one version at a
time until it reaches the version the installed extension writes. A file with no stamp at all, or with a
stamp that is not a version, reads as empty rather than failing.

A file stamped *newer* than the installed extension understands is never overwritten. It is renamed to
`favorites.tsv.v<version>.bak` first, favorites start empty, and a message box says where it went; an
existing backup of the same version is not clobbered either. If that rename fails, nothing is written at
all for the rest of the session, because keeping the unreadable file intact matters more than saving a
star into it.

There is no limit and nothing is evicted: every star is a deliberate click, so the only thing a cap
could do is throw away a query you meant to keep. They are listed most recently starred first. The
list re-sorts on the next run rather than under the cursor, so an accidental star can be clicked
straight back off.

The **pencil** renames the row in place. The box starts on the name, or on the whole predicate when there
is no name yet, so a name is an edit of the predicate rather than a blank field; Enter commits, Escape
abandons, and clicking away commits. A named row shows the name where the predicate was and keeps the
predicate as its tooltip, and double-clicking still restores the predicate itself. Emptying the box and
committing brings the predicate back as the row's text, and so does typing it back verbatim. A name is
stored next to the star, so it lasts across restarts while the row is starred and only for the session
otherwise.

The **cross** drops the row from the sidebar, unstarring it if it was starred. It deliberately leaves
the compiled delegate in the cache; see above.

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

A second window, modelled on ILSpy's **Analyze** pane: pick a symbol and it grows a tree of everything
that relates to it, and every result in that tree can be analyzed in turn.

Right-click a symbol in the editor and choose **View Reference Graph**, or open the window empty from
`View > Other Windows > Reference Graph`. Almost anything the caret can sit on works as a root: a
method, constructor, operator, property, indexer, field, event or type, and also a namespace, a local,
a parameter, a type parameter, a local function or a lambda.

Each invocation adds a root at the top of the list rather than replacing what is there, so the window
keeps a history; **Clear** empties it, and Del removes just the selected root.

### Branches

Under a symbol sit the branches that apply to it, and only those - a static method offers two, an
override offers several. Opening a branch runs its search, and the header then reads
`Used By (18 in 931 ms)`: how many results it found and how long that took. A branch that finds
nothing keeps its header but loses its expander.

| Branch            | What is under it                                                                          | Offered on                                                                       |
| ----------------- | ----------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------- |
| Uses              | What the symbol's own declaration references                                              | Members, types, local functions, lambdas                                         |
| Used By           | The declarations that reference it                                                        | Methods, properties, events, types, namespaces, type parameters, local functions |
| Read By           | The declarations that read it                                                             | Fields, enum members, locals, parameters                                         |
| Assigned By       | The declarations that write it                                                            | Fields, locals, parameters                                                       |
| Instantiated By   | The declarations that construct it                                                        | Classes, structs, delegates                                                      |
| Exposed By        | The members whose signature names it - a parameter, return or member type, or a base list | Types                                                                            |
| Applied To        | The declarations it is applied to as an attribute                                         | Attribute classes                                                                |
| Overrides         | The member it overrides, then that member's own base, nearest first                       | Overrides                                                                        |
| Overridden By     | Every override below it                                                                   | Virtual, abstract and unsealed override members                                  |
| Implements        | The interface members it implements                                                       | Members that implement an interface                                              |
| Implemented By    | The members or types that implement it                                                    | Interfaces and interface members                                                 |
| Derived Types     | Every class or interface that derives from it                                             | Classes and interfaces                                                           |
| Extension Methods | The extension methods that apply to it                                                    | Types                                                                            |
| Contains          | Its sub-namespaces, then its types                                                        | Namespaces                                                                       |

Every result is a symbol of its own and offers its own branches, so the tree can be followed as far as
it goes: from a method into what it uses, from one of those into what uses it, and on from there. A
result whose symbol already appears above it is marked `(recursive)` and offers nothing further, so a
cycle terminates instead of looping.

A result is one declaration, not one call site; its second line reads `3 refs (1 invocation, 2 reads)`.
A result backed by more than one occurrence opens onto a `Locations (3)` row listing each of them,
ahead of its branches. A branch shows every result it finds: the tree is virtualized, and a collapsed
remainder row would be a dead end you could not expand.

Rows are spelled the way ILSpy spells them, `System.IO.MemoryStream.Read(byte[], int, int) : int`, in
the editor's own colours, and a local, parameter, local function or lambda is qualified by the members
around it.

Double-click a row, or select it and press Enter, to jump to it; a location row goes to that exact
occurrence. A compound assignment such as `x += 1`, or a `ref` argument, is both a read and a write
and shows under both; `++`, `--`, an `out` argument and `+=` on an event count as writes. A `cref` in
a doc comment is never counted as a use.

A row for a local, parameter, type parameter, local function or lambda is tied to where it is declared
in the file, since nothing else identifies it. Editing the file above that declaration makes the row
stale: **Refresh** then reports it as gone, and it has to be rooted again.

### Symbols from referenced assemblies

Results are not limited to your own code. `Overrides`, `Overridden By`, `Implements`,
`Implemented By` and `Derived Types` search referenced assemblies too, so `Derived Types` on `Stream`
lists `MemoryStream` and the rest. Such a row is marked `(metadata)`. It offers every branch except
`Uses`, since there is no source to read what it uses, and its `Used By` and similar branches find the
uses in your own code.

Double-clicking a metadata row opens the symbol in **ILSpy**, positioned on the member. Either way of
opening one needs the implementation assembly: a project references reference assemblies, which carry
no method bodies, so the window first finds the implementation behind one - in the shared runtime for
.NET, in the GAC for .NET Framework, beside it under `lib` for a NuGet package - and when there is none
it says so rather than open an empty stub. A metadata row's call sites in your own code stay reachable
through its `Locations` row.

ILSpy is found automatically: a standalone install first, then the copy inside the ILSpy extension for
Visual Studio. That search runs once per session, so set `ILSpy path` if yours lives somewhere else.
Launches reuse one ILSpy window rather than opening a new one each time. When no ILSpy can be found, or
when `ILSpy path` points at something that is not there, a message box says so rather than quietly
decompiling instead; changing either setting re-runs the search.

Switching `Open metadata symbols in` to Visual Studio opens **decompiled source** in a read-only editor
tab instead, decompiled by the ICSharpCode.Decompiler that Visual Studio itself ships. That file opens
as an ordinary file, so the editor may underline types it cannot resolve.

### Scope

The **scope** combo - current document, current project, my solution - narrows the branches that
search: `Used By`, `Read By`, `Assigned By`, `Instantiated By`, `Exposed By` and `Applied To` to the
documents it covers, and `Overridden By`, `Implements`, `Implemented By` and `Derived Types` to the
projects it covers. `Uses` is read out of the row's own declaration, and `Overrides`,
`Extension Methods` and `Contains` are not narrowed at all. Scope is measured from the declaration of
the row being expanded, not from wherever the caret happens to be at the time.

**Refresh** and changing the scope re-read every expanded branch. **Stop** cancels whatever is in
flight and clears every expanded branch, since a cancelled search may have left it stale; expand a
branch again to re-read it.

The scope the window starts on is configurable; see `Default scope` below.

### Settings

`Tools > Options > RoslynQuery > Reference Graph` holds:

| Setting                      | Meaning                                                                                                                                           |
| ---------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Default scope`              | The scope the Scope box starts on each time the window loads. Defaults to `Current project`.                                                      |
| `Open metadata symbols in`   | `Open in ILSpy` (the default) or `Decompile in Visual Studio`.                                                                                    |
| `ILSpy path`                 | Full path to `ILSpy.exe`. Empty means search for one once per session. A path that does not exist raises a message box rather than being ignored. |
| `Enable IL analysis`         | Not available yet - shown disabled, does nothing.                                                                                                 |
| `Enable reverse IL analysis` | Not available yet - shown disabled, does nothing.                                                                                                 |

The two IL analysis settings would read the IL of framework methods so that `Uses` could continue past
your source and `Used By` could report framework callers.

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
