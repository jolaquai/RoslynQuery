# Agent integration: manual test plan (Windows, real Visual Studio)

Target: branch `claude/dreamy-noether-ocs4ea`, issue #9. CI already proves the build, unit tests and VSIX packaging;
this plan covers what CI cannot: the bundled CLI at runtime, the in-VS Agent Bridge, MCP, and the result context menu.
Do not change code to make a test pass: record the failure (see "Reporting") and continue.

## 0. Setup

1. Machine: Windows with Visual Studio (version the extension targets) and the .NET SDK. A second VS instance and a
   Windows-on-ARM box are optional (tests marked [2VS], [ARM]).
2. Get the VSIX: latest green run on the branch (https://github.com/jolaquai/RoslynQuery/actions), artifact `vsix`
   (`gh run download <run-id> -n vsix -D vsix`). Uninstall any older RoslynQuery first (Extensions > Manage), close VS,
   double-click the `.vsix`, reopen VS.
3. Test solution: clone this repo and open `RoslynQuery.slnx` in VS. It has C# of every shape needed and a test project.
   Keep a second scratch copy of a small solution (`git worktree` or a copy) for destructive replace tests.
4. Work in a normal terminal unless a test says "VS terminal" (View > Terminal, started by devenv).
5. Locate the CLI: Tools > Roslyn Query > **Copy CLI path**, paste it. Also readable in
   `%LOCALAPPDATA%\RoslynQuery\cli-path.txt`. Expected: `...\Cli\win-x64\roslynquery.exe` (or `win-arm64` on ARM).
   Set `$cli` to it. Every `roslynquery` below means `& $cli`.

## 1. Headless CLI (VS closed or `--no-attach`)

Always use `--no-attach` here. Output is one JSON line on stdout; diagnostics go to stderr.

| # | Command | Expected |
|---|---------|----------|
| 1.1 | `roslynquery` (no args) | usage on stdout, exit 2. `roslynquery --help` exit 0. |
| 1.2 | `roslynquery docs` | markdown; mentions predicate parameters per target, `## Live Visual Studio`, replace/`planId`. Signatures printed match the real code (`-t node` shows `SyntaxNode n, SemanticModel model, ...`). |
| 1.3 | `roslynquery schema` | JSON with `request` and `response` schemas; enum names (`SyntaxToken`, `ContainingType`) present. |
| 1.4 | `roslynquery run -s RoslynQuery.slnx --no-attach -q "n is MethodDeclarationSyntax m && m.Identifier.Text == \"Main\""` | `ok:true`, `source:"headless"`, hits with `file` relative to the solution, 1-based `line/column/endLine/endColumn`, `kind`, `preview`. Hits sorted by file then position. |
| 1.5 | Compile error: `-q "n is Foo"` | `ok:false`, `error` starts `Compile error: col`, returns within ~2 s (predicate compiles BEFORE the solution loads), exit 1. |
| 1.6 | Targets: `-t token -q "t.IsKind(SyntaxKind.AsyncKeyword)"`, `-t operation -q "op is IInvocationOperation i && i.TargetMethod.Name == \"Format\""` | hits for each; operation query exercises the semantic model (`model` bound only when named). |
| 1.7 | Return-node form: `-q "n is MethodDeclarationSyntax m ? m.Identifier : null"` | hit spans the identifier token, not the method. |
| 1.8 | Scopes: `--scope project -p RoslynQuery.Cli`, `-f <file> ` (document), `-f <file> --line 20 --column 5 --scope type` / `member` | hits only inside the scope; wrong project/file -> `ok:false`, clear message, exit 1. A multi-targeted project is one scope. |
| 1.9 | `--max 3` on a query with many hits | 3 hits, `stats.truncated:true`. |
| 1.10 | Generated code: query something in `obj/` or `*.g.cs` | skipped by default; present with `--include-generated`. |
| 1.11 | stdin: `"<query>" \| roslynquery run -s ... -q -` and `--query-file`, `--request <json>` (flags override it) | same result as `-q`. Two `-` stdin sources -> usage error, exit 2. |
| 1.12 | Bad input: unknown flag, `--line abc`, malformed `--request` JSON | message on stderr + usage, exit 2, no stack trace. |
| 1.13 | No `-s` in a folder with exactly one sln/slnx/csproj, then with several | uses the single one; several -> clear error. |
| 1.14 | `stats.errors` / `firstError`: `-q "model.GetSymbolInfo(n.Parent.Parent.Parent).Symbol.Name == \"x\""` | doesn't abort; counts predicate exceptions, shows first. |

### 1R. Replace (headless), on the scratch copy only

| # | Steps | Expected |
|---|-------|----------|
| R.1 | Dry run: `-q <invocations of Foo> -r "\"Bar\" + n.ToString().Substring(3)"` | `replacements[]` and `changes[]` (unified diff per file with `---/+++`, hunks, no trailing phantom blank line), `planId` (16 hex), `applied` absent/false, files on disk untouched. |
| R.2 | Same + `--apply --expect <planId>` | `applied:true`; files changed exactly per the diff; `git diff` agrees. |
| R.3 | Re-run R.2 immediately | no matches -> nothing to apply, no corruption. |
| R.4 | `--apply --expect 0000000000000000` | refused, error mentions plan mismatch, **exit 3**, nothing written. |
| R.5 | Dry run, edit one affected file by hand, then `--apply --expect <planId>` | refused (staleness or mismatch), exit 3, nothing written. |
| R.6 | `--apply` without `-r` | `ok:false` ("Apply needs a Replace"). `-t operation -r ...` -> not supported error. |
| R.7 | Replace returning `null` for some hits; overlapping matches | skipped hits listed with a warning; no overlapping edit written. |
| R.8 | Files with UTF-8 BOM, UTF-8 no BOM, CRLF, LF | after apply, BOM/encoding and line endings preserved (check bytes: `Format-Hex`). Formatting of changed regions follows `.editorconfig`. |
| R.9 | Multi-targeted project (same file in two Roslyn projects) | file reported/changed once. |

## 2. Agent Bridge (in VS)

1. Tools > **Roslyn Query** menu exists with: enable/disable bridge, restart, show log, copy CLI path. Bridge is OFF by default.
2. Enable. Expected: log (Show log, Output pane) says listening; file `%LOCALAPPDATA%\RoslynQuery\bridge\<devenv pid>.json` exists
   (pid, pipe, token, solution). Opening/closing a solution rewrites `solution` in it.
3. Run 1.4 *without* `--no-attach` from a normal terminal. Expected: `source:"vs"`. `--attach` works too; with the bridge
   disabled `--attach` fails with the "No Visual Studio bridge" message (exit 1) and plain `run` falls back to `headless`.
4. Unsaved edits: in VS add a method `ZzTestMarker()` (do not save). Query for it via CLI: found when attached,
   NOT found with `--no-attach`.
5. Replace through the bridge: dry run then `--apply --expect <planId>` on the scratch solution open in VS. Expected:
   edit appears in the editor as ONE undo unit (Ctrl+Z reverts every file), planId guard works the same as headless
   (mismatch -> exit 3). Dirty documents / files changed in the editor after the dry run -> refused, not clobbered.
6. Disable -> the pipe and host file disappear; CLI falls back to headless. Re-enable; **Restart** keeps working.
   Close VS with bridge on: host file removed (or cleaned up as dead pid by the next CLI call).
7. Remembered state: enable, close VS, reopen: bridge auto-starts (option `AutoStartAgentBridge`). Disable, restart VS: stays off.
8. Auth (security): from PowerShell open `\\.\pipe\<pipe name from host file>` with `NamedPipeClientStream`, send a WRONG
   token line then a request. Expected: rejected, no workspace access. Pipe ACL is current-user only
   (`icacls`-style check not possible on pipes; confirm via a second Windows user if available).
9. Concurrency: fire 3 queries at once (`1..3 | % { Start-Job { & $cli run ... } }`). Expected: all succeed (serialized by the bridge, no UI freeze > a few seconds, no deadlock).
10. Large query: whole-solution token query with `--max 100000` while typing in VS: VS stays responsive; cancel with Ctrl+C on CLI leaves bridge healthy.
11. [2VS] Two VS instances, same solution (or same folder) open. From the *VS terminal* of instance A, `run` must report
    instance A (check `source` pid via `roslynquery mcp`'s `bridges` tool, or host-file pid vs `$PID` ancestry). From a normal
    terminal: best solution match, then newest. Repeat from B's terminal. Different solutions: exact solution match wins over
    same folder over cwd-under-solution.

## 3. MCP server (`roslynquery mcp`, stdio)

Use any MCP client (e.g. `npx @modelcontextprotocol/inspector <cli> mcp`) or an agent's MCP config:
`{"command":"<cli path>","args":["mcp"]}`.

| # | Check | Expected |
|---|-------|----------|
| 3.1 | initialize + tools/list | exactly `search` (read-only), `replace` (destructive), `docs` (read-only), `bridges` (read-only), each with description and JSON input schema. |
| 3.2 | stdout cleanliness | only protocol frames on stdout; all logging on stderr (a stray line on stdout breaks clients: check with a raw `<cli> mcp` and a pasted `initialize` request). |
| 3.3 | `docs` | same content as `roslynquery docs`. |
| 3.4 | `bridges` | lists running VS bridges (pid, solution, whether it is this process's ancestor); empty list when none. |
| 3.5 | `search` | same result shape as the CLI; compile errors come back as a normal tool result with `ok:false`, not a protocol error. |
| 3.6 | `replace` dry run then apply with the returned `planId` | same semantics as 1R/2.5; apply without a matching `planId` refused. |
| 3.7 | Started from VS terminal | attaches to that VS (`source:"vs"`); started elsewhere, VS closed -> headless. |
| 3.8 | Cancel a long search from the client | server stays alive, next call works. |
| 3.9 | End to end with a real agent (Claude Code: `claude mcp add roslynquery -- <cli> mcp`): ask "find all async methods without await in this solution and show me the diff of removing the async modifier" | agent uses `docs`/`search`/`replace`, shows diff, applies only after confirmation. |

## 4. Result context menu (Query tool window)

Run any search in the tool window (View > Other Windows > Roslyn Query), then in BOTH the Results and Replace-results lists:

1. Right-click a result: the row under the cursor becomes selected; menu shows **Copy**, **Copy location**, **Copy location (MSBuild)**, themed (dark + light VS themes).
2. Copy -> the match text. Copy location -> `C:\full\path\File.cs:12` (1-based line, Code Search style).
3. Copy location (MSBuild) formats, verify by pasting: single-line span `File.cs(12,5-18)`; multi-line span `File.cs(12,5,14,3)`;
   empty span `File.cs(12,5)`. End column is exclusive.
4. Right-click on empty list area: no crash. Multiple selection: menu acts on the clicked row.

## 5. Regression / smoke

- Existing tool window: search, replace preview, apply, Ctrl+Z, result navigation (double-click) all still work.
- `RoslynQuery.Tests` run green locally: `dotnet build RoslynQuery.Tests -c Release; .\RoslynQuery.Tests\bin\Release\net472\RoslynQuery.Tests.exe`.
- VS startup time / package load unaffected with the bridge off (bridge must not start by default).
- Install on a clean VS profile (`devenv /rootsuffix Exp` or a VM): extension loads, no MEF composition errors in
  `%APPDATA%\Microsoft\VisualStudio\<ver>\ActivityLog.xml` (look for RoslynQuery / duplicate assembly identity).
- [ARM] On Windows on ARM, `Copy CLI path` yields `win-arm64` and 1.4 works.
- Uninstall: bridge host files left behind are harmless; CLI folder removed with the extension.

## Reporting

Return a table: test id | PASS/FAIL/SKIP | evidence (command, trimmed output, screenshot path). For every FAIL include exact
command, full stdout/stderr, VS version, `%LOCALAPPDATA%\RoslynQuery\bridge\*.json` (token redacted) and the bridge log.
Known unverified-by-anyone areas (highest bug likelihood): bridge apply + undo (2.5), pipe auth (2.8), ancestry attach (2.11),
MCP stdout cleanliness (3.2), context-menu theming (4.1), `.editorconfig` formatting on apply (R.8).
Do not open a PR or push; post findings as a comment on issue #9 only if asked.
