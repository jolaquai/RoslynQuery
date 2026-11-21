using System.Text;

using RoslynQuery.Protocol;
using RoslynQuery.Query;
using RoslynQuery.Replace;

namespace RoslynQuery.Cli;

/// <summary>
/// The agent-facing documentation. Everything that can drift (signatures, imports) is read from the same
/// code that compiles the predicates, so this cannot disagree with the binary it ships in.
/// </summary>
internal static class Docs
{
    public static string Build()
    {
        var sb = new StringBuilder();

        sb.AppendLine("""
            # roslynquery

            Runs a C# predicate over every syntax node, token or operation of a solution, using Roslyn, and prints
            JSON. Optionally rewrites the matches. Prefer this over grep when the question is about syntax or
            semantics (calls to a specific overload, `async` methods without `await`, attribute usage, ...).

            ```
            roslynquery run -s <sln> -q "<predicate>" [-t node|token|operation] [--scope ...] [-r "<replacement>" [--apply]]
            ```

            `roslynquery schema` prints the JSON Schemas of the request (`--request <file|->`, enums spelled `SyntaxToken`,
            `ContainingType`, ...) and the response.
            The predicate is compiled before the solution loads, so a compile error returns within seconds:
            `{"ok":false,"error":"Compile error: col 12: CS1061: ..."}`.

            ## Predicate

            One C# bool expression, or statements that `return` (use statements when you need locals or loops).
            `await` works. It runs once per candidate; parameters by target:
            """);

        sb.AppendLine();
        foreach (var kind in (TargetKind[])[TargetKind.SyntaxNode, TargetKind.SyntaxToken, TargetKind.Operation])
            sb.Append("- `-t ").Append(kind switch { TargetKind.SyntaxNode => "node", TargetKind.SyntaxToken => "token", _ => "operation" })
              .Append("`: `").Append(PredicateTemplate.Signature(kind)).AppendLine("`");

        sb.AppendLine().AppendLine("""
            `model` is only bound when the text mentions `model` (it is the expensive part), and is `null` for
            documents without semantic support.

            Return `true`/`false`, `null` (no match), or a node/token/operation from the same tree to report *that*
            as the hit instead (for example a method's identifier token rather than the whole method).

            Already imported:

            ```csharp
            """);
        sb.AppendLine(PredicateTemplate.Usings);
        sb.AppendLine("```");

        sb.AppendLine("""

            ## Live Visual Studio

            When Visual Studio has the solution open and its bridge is enabled (Extensions > Roslyn Query > Agent Bridge),
            `run` talks to it by default: the same request runs against the live workspace, unsaved edits included,
            and an `--apply` becomes one undoable edit in the editor. The response says `"source":"vs"`; without a
            bridge it is `"headless"` (solution loaded from disk, ~seconds). `--no-attach` / `--attach` force either.
            Pass the solution Visual Studio has open as `solution` (`-s`): without it, a process not started from
            inside Visual Studio only attaches when the working directory is in the solution or one instance is open.
            `bridgePid` names the instance that answered; `warnings` say when the run was headless although a bridge
            was open, or when several instances matched equally well. `bridges` lists the open instances.

            ## Scope

            `--scope solution` (default), `project` (`-p <name|path>`, or `-f` a file in it), `document` (`-f`),
            `type`/`member` (`-f <file> --line <n> --column <n>`, 1-based, the declaration around that position).
            Generated code is skipped unless `--include-generated`. Results are capped by `--max` (default 200);
            `stats.truncated` says more exist. Prefer narrowing the predicate or the scope over raising the cap.

            ## Output

            `hits[]`: `file` (relative to the solution), `line`/`column`/`endLine`/`endColumn` (1-based), `kind`, `preview`.
            `stats.errors` counts candidates the predicate threw on (`stats.firstError` shows why); a wide
            `model.GetSymbolInfo(...)` over code that does not bind is the usual cause.
            `workspaceWarnings` non-empty means the solution did not load cleanly (missing SDK, restore not run);
            matches can be incomplete. Run `dotnet restore` first.

            ## Replace

            `-r` takes the same kind of text as the predicate, evaluated once per hit, with the same parameters (not `operation`):
            """);
        sb.AppendLine();
        foreach (var kind in (TargetKind[])[TargetKind.SyntaxNode, TargetKind.SyntaxToken])
            sb.Append("- `").Append(ReplaceTemplate.Signature(kind)).AppendLine("`");

        sb.AppendLine("""

            Return a `string` (the new source text), a `SyntaxNode` (normalized), a `SyntaxToken` (its text), or
            `null` to leave the match alone. Matches that overlap another one in the same file are skipped with a warning.

            Without `--apply` this is a dry run: `changes[]` holds one unified diff per file and `planId` identifies
            exactly what would be written. Review the diff, then run the same command with
            `--apply --expect <planId>`. If a file or the query changed in between, nothing is written (exit code 3).
            Formatting of the changed regions follows the repository's `.editorconfig`.

            ## Examples

            ```
            # every async method that never awaits
            roslynquery run -s App.slnx -q "n is MethodDeclarationSyntax m && m.Modifiers.Any(SyntaxKind.AsyncKeyword) && !m.DescendantNodes().OfType<AwaitExpressionSyntax>().Any()"

            # calls to string.Format, resolved semantically
            roslynquery run -s App.slnx -q "n is InvocationExpressionSyntax && model.GetSymbolInfo(n).Symbol is IMethodSymbol { Name: \"Format\", ContainingType.SpecialType: SpecialType.System_String }"

            # dry run, then apply, of a rename of Foo() calls
            roslynquery run -s App.slnx -q "n is InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: \"Foo\" } }" -r "\"Bar\" + n.ToString().Substring(3)"
            roslynquery run -s App.slnx -q "..." -r "..." --apply --expect <planId>
            ```
            """);

        return sb.ToString();
    }
}
