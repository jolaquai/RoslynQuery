# RoslynQuery MCP Bridge — remaining work

Branch: `claude/mcp-bridge` (built on top of `claude/mcp-server-code-transforms-mjvjmx`,
which is fully merged into its history). Neither branch has a PR open yet. CI green as of
`b315b0d`.

## Untested — needs a real Windows + Visual Studio

Nothing below has ever run in an actual VS instance. CI proves compile, packaging shape, and
the search/RPC logic in isolation against a fake workspace — none of it proves the extension
works once installed.

1. **F5 the extension** and confirm:
   - Roslyn Query / Reference Graph tool windows still behave as before (nothing here should
     have changed their behavior).
   - Tools > Options > RoslynQuery > General shows all five settings (default target, scope,
     cap, include-generated, show-history) as sane PropertyGrid controls, and that changing one
     + restarting VS actually persists it.
   - The Search tool window's Scope/Target/Cap/Generated/History-sidebar state on open matches
     whatever's set in Options.
2. **StreamJsonRpc at runtime** — `RoslynQuery.csproj` excludes StreamJsonRpc's runtime assets
   on the assumption devenv already has a copy loaded, the same pattern used for Roslyn. Never
   confirmed. If wrong, the MCP bridge's startup throws (caught, doesn't crash the extension,
   just logged) — check the Debug Output window in the experimental instance for
   `"RoslynQuery: MCP bridge failed to start:"`.
3. **Broker extraction path** — nothing declares `Broker/**` in the `.vsixmanifest`; it's just
   extra entries in the `.vsix` zip. Confirm the installed extension's folder
   (`%LocalAppData%\Microsoft\VisualStudio\<ver>\Extensions\...`) actually contains
   `Broker/win-x64/RoslynQuery.Mcp.Broker.exe` (or `win-arm64`) next to `RoslynQuery.dll`.
4. **Broker actually launches** — open a solution, check Task Manager for
   `RoslynQuery.Mcp.Broker.exe`. If it's missing, either #3 didn't hold or the spawn failed
   silently.
5. **The actual MCP round trip** — point a real MCP client at `http://localhost:5050` (Claude
   Code itself, or any HTTP-transport client) and call `roslynquery_search`. This has never
   happened even once; everything up to here is inference from CI plus `PipeHostTests`, which
   only proves the pipe/RPC leg against a fake in-memory workspace.

## Explicitly not built yet

- **Replace over MCP** (`roslynquery_replace_preview` / `roslynquery_replace_apply`) — only
  Search shipped. Needs the preview cache below first.
- **Preview cache** for the two-step preview-then-apply-by-id flow: how a `PreviewId` maps back
  to the `ReplacementItem`s and the `Solution` they were generated against, plus its
  TTL/eviction. Open design question, not started.
- **Multi-VS-instance handling** — the broker's HTTP port is a hardcoded `5050`; two VS windows
  open at once will collide on it. The pipe name is already per-instance (keyed on devenv's
  PID); the port isn't.
- **Reference Graph's own Scope combo** isn't wired to `RoslynQueryOptions` — still hardcodes to
  Project. Only the Search/Replace window's defaults were asked for.

## Known, accepted tradeoffs (not bugs)

- VSIX size roughly doubles-to-triples: two embedded self-contained single-file broker builds
  (win-x64 + win-arm64), each typically 60-100MB even with `InvariantGlobalization` on.
- No auth on the broker's HTTP endpoint beyond loopback bind + Host-header check + CORS
  restricted to localhost — same-machine trust model, same as Claude Code already editing files
  directly.
- Predicates and replacements are compiled and executed as live C# — an MCP tool call is
  "compile and run this against your solution," not a sandboxed query.

## Test coverage as it stands

- `PipeHostTests.cs`: a real named pipe, a real `JsonRpc.Attach<IRoslynQueryRpc>` client, and a
  real `SearchAsync` call, against an `AdhocWorkspace`. Covers the RPC/wire path for Search only.
- Nothing tests the broker's own ASP.NET Core/Kestrel layer (`Program.cs`, `RoslynQueryTools.cs`)
  — would need a separate net11 test project, since `RoslynQuery.Tests` targets net472 and can't
  host `WebApplicationFactory`-style tests.
- Nothing tests `BrokerProcess` (spawn/kill) or the VSIX's installed-and-running behavior —
  neither is reachable from a unit test.

## CI, for reference

`.github/workflows/release.yml` verifies: `RoslynQuery.csproj` compiles, `RoslynQuery.Tests`
passes (`PipeHostTests` included), `RoslynQuery.Mcp.Broker` publishes self-contained for both
RIDs, and the resulting `.vsix` contains exactly `RoslynQuery.dll` plus the two broker exes
under `Broker/<rid>/`. It cannot and does not install the VSIX or start a real VS.
