# Roslyn Query

Two Visual Studio tool windows over Roslyn.

**Roslyn Query** runs a C# predicate you write over every `SyntaxNode`, `SyntaxToken` or
`IOperation` in a scope you pick, and jumps to a double-clicked match.
`View > Other Windows > Roslyn Query`.

**Reference Graph** roots a lazily-expandable tree on the member or type at the caret, showing what
references it and what it references, recursively.
`View > Other Windows > Reference Graph`, or right-click in the editor.

## Using it

Pick a **Target**, pick a **Scope**, type a predicate, press Enter (or Run).

The signature line above the box tells you what is in scope:

| Target      | Predicate signature                                                        |
| ----------- | -------------------------------------------------------------------------- |
| SyntaxNode  | `async ValueTask<bool> (SyntaxNode n, SemanticModel model, Document doc)`  |
| SyntaxToken | `async ValueTask<bool> (SyntaxToken t, SemanticModel model, Document doc)` |
| IOperation  | `async ValueTask<bool> (IOperation op, SemanticModel model, Document doc)` |

`System`, `System.Collections.Generic`, `System.Collections.Immutable`, `System.Linq`,
`System.Text`, `System.Text.RegularExpressions`, `System.Threading.Tasks`,
`Microsoft.CodeAnalysis`, `.CSharp`, `.CSharp.Syntax`, `.Operations` and `.Text` are already
imported.

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

| Key                   | Effect                                                             |
| --------------------- | ------------------------------------------------------------------ |
| Enter                 | run the query (or commit the completion item, if the list is open) |
| Shift+Enter           | newline in the predicate                                           |
| Ctrl+Space            | invoke completion                                                  |
| Up / Down             | move through the completion list, otherwise move the caret         |
| Tab                   | commit the completion item, otherwise leave the box                |
| Esc                   | dismiss the completion list, never leaves the box                  |
| `.` `(` `,` operators | commit the completion item, then type the character                |
| Double-click a result | open the file and select the match                                 |
| Double-click history  | restore that predicate and re-run it                               |

The predicate box is a real editor view, not a `TextBox`, and it is not wired into VS's command
routing, so its keyboard map is implemented by the extension. Caret movement, selection, backspace
and delete (with the Ctrl word-wise variants), Ctrl+A/C/X/V and Ctrl+Z/Y all work; anything beyond
that is not bound.

The commit characters are Roslyn's C# set without the space, so `n.IsK` + `(` commits `IsKind` and
gives you `n.IsKind(`, but a space just types a space. Nothing commits while the selection is soft,
which is what the list shows right after a bare Ctrl+Space.

Every run is explicit: Enter or the Run button. There is no run-as-you-type mode, on purpose. Each
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

## Reference Graph

A second window, styled after the built-in Call Hierarchy but generalized to every kind of
reference rather than just calls.

Right-click a method, constructor, property, field, event or type in the editor and choose
**View Reference Graph**, or open the window empty from `View > Other Windows > Reference Graph`.

Each invocation adds a root at the top of the list rather than replacing what is there, so the
window keeps a history; **Clear** empties it. Every root has two branches:

| Branch                | What is under it                                             |
| --------------------- | ------------------------------------------------------------ |
| `References To 'X'`   | The declarations that reference `X`                          |
| `References From 'X'` | The declarations `X` itself references                       |

Every row expands the same way, recursively, staying in the direction its branch started in. A row
is one declaration, not one call site: its second line reads `3 refs (1 invocation, 2 reads)`, and
double-clicking navigates to the first of them. A row whose symbol already appears above it in the
tree is marked `(recursive)` and does not expand further, so a cycle terminates instead of looping.

### Scope

The **scope** combo - current document, current project, my solution - narrows `References To`
only. `References From` is read out of the root's own declarations and never searches outside them,
so there is nothing for a scope to narrow. Scope is measured from the declaration of the row being
expanded, not from wherever the caret happens to be at the time.

### Usage kinds

The **Filter** button opens a checkbox flyout, one box per kind of reference:

| Kind             | What it matches                                                          |
| ---------------- | ------------------------------------------------------------------------ |
| Invocations      | The callee of a call                                                     |
| Reads            | Anything read, including a method group                                  |
| Writes           | An assignment target, an `out`/`ref` argument, `++`/`--`, `+=` on events |
| Constructions    | `new T(...)`, a `this()`/`base()` initializer, an attribute              |
| Type references  | A parameter or return type, a base type, a cast, `typeof`, a type argument, a `catch` clause |

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
