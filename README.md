# Roslyn Query

A Visual Studio tool window that runs a C# predicate you write over every `SyntaxNode`,
`SyntaxToken` or `IOperation` in a scope you pick, and jumps to a double-clicked match.

`View > Other Windows > Roslyn Query`.

## Using it

Pick a **Target**, pick a **Scope**, type a boolean expression, press Enter (or Run).

The signature line above the box tells you what is in scope:

| Target      | Predicate signature                                       |
| ----------- | --------------------------------------------------------- |
| SyntaxNode  | `bool (SyntaxNode n, SemanticModel model, Document doc)`  |
| SyntaxToken | `bool (SyntaxToken t, SemanticModel model, Document doc)` |
| IOperation  | `bool (IOperation op, SemanticModel model, Document doc)` |

`System`, `System.Linq`, `System.Text.RegularExpressions`, `Microsoft.CodeAnalysis`,
`.CSharp`, `.CSharp.Syntax`, `.Operations` and `.Text` are already imported.

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
op.Kind == OperationKind.Conversion && ((IConversionOperation)op).Conversion.IsBoxing
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
| Esc                   | dismiss the completion list                                        |
| Double-click a result | open the file and select the match                                 |

The predicate box is a real editor view, not a `TextBox`, and it is not wired into VS's command
routing, so its keyboard map is implemented by the extension. Caret movement, selection, backspace
and delete (with the Ctrl word-wise variants), Ctrl+A/C/X/V and Ctrl+Z/Y all work; anything beyond
that is not bound.

Every run is explicit: Enter or the Run button. There is no run-as-you-type mode, on purpose. Each
distinct expression leaks a small assembly for the session (see below), and a debounced re-run turns
that into one leak per pause in your typing.

**Cap** stops the run once that many matches are found and marks the results as capped.

**Generated** includes `.g.cs` / `.designer.cs` / `.generated.cs` and source-generated documents.
Source-generated matches have no file on disk, so double-click reports that instead of navigating.

## Building

Requires the Visual Studio SDK workload.

```bash
msbuild RoslynQuery/RoslynQuery.csproj -t:Rebuild -p:Configuration=Release -p:DeployExtension=false
```

The `.vsix` lands in `RoslynQuery/bin/Release/net472/`. F5 on the project launches the experimental
instance with the extension deployed.

Targets VS 2022 and VS 2026 from one artifact. See [CLAUDE.roslyn-query.md](CLAUDE.roslyn-query.md)
for the design, the rejected alternatives and the deferred work.
