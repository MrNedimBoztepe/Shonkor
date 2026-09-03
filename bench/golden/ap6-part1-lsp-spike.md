# AP6 part 1 / 0.3 — LSP spike: headless Roslyn against the graph's SemanticSymbol edges (#467)

Time-boxed spike. Question: can single-language C# navigation be delegated to a language server, and how
far do its answers differ from the graph's `SemanticSymbol` edges? Executed under
`ap6-precision-evidence-policy.md`; sibling of `ap6-part1-positioning.md`.

**Mapping code stays in `Shonkor.Bench`** (`LspClient.cs`, `LspDiff.cs`, `LspDiffRunner.cs`,
`LspDiffReport.cs`). Promotion to Core is a core-cut decision, not a spike result.

## 1. Server and licence — decided before the first request

| | |
|---|---|
| Server | `roslyn-language-server` **5.12.0-1.26426.8** (`dotnet tool install -g roslyn-language-server --prerelease`), the thin client bundling `Microsoft.CodeAnalysis.LanguageServer.exe` (dotnet/roslyn commit `3aeb96c9`) |
| Licence field in the `.nupkg` | `<license type="expression">MIT</license>`, `licenseUrl https://licenses.nuget.org/MIT`, `<requireLicenseAcceptance>true</requireLicenseAcceptance>` — the acceptance flag points at the MIT text, not at a separate EULA |
| Files in the package besides binaries | `README.md`, `ThirdPartyNotices.rtf` (opens with "The MIT License (MIT), Copyright (c) .NET Foundation and Contributors"), `Icon.png`. **No EULA, no `LICENSE.txt` with other terms.** Checked in both `roslyn-language-server` and `roslyn-language-server.win-x64` nuspecs |
| Decision | MIT confirmed on the actual artifact; the proprietary part (C# Dev Kit) is not involved. Fallback `csharp-ls` was **not needed** (Step 0 below). OmniSharp-Roslyn excluded per ticket (no call hierarchy). |
| Assumption left open | `--telemetryLevel` defaults to `off` per README; not verified on the wire. |

Windows shim: `%USERPROFILE%\.dotnet\tools\roslyn-language-server.cmd` → the real exe under
`.store\roslyn-language-server\5.12.0-1.26426.8\roslyn-language-server.win-x64\5.12.0-1.26426.8\tools\net10.0\win-x64\roslyn-language-server.exe`.
The diff was run against the exe directly with `--stdio --logLevel Information --extensionLogDirectory <dir>`.

## 2. Step 0 — `initialize` result

Client capabilities sent: `documentSymbol.hierarchicalDocumentSymbolSupport=true`, `callHierarchy`,
`implementation`, `references`, `workspace.configuration`, `workspace.workspaceFolders`; **no**
`dynamicRegistration` anywhere, so every provider had to be static. Zero `client/registerCapability`
requests arrived.

`capabilities` keys returned (full dump in `bench/lsp-diff.md` after every run):

```
_vs_onAutoInsertProvider {…}   textDocumentSync {openClose, change:2, save}   completionProvider {…}
hoverProvider true             signatureHelpProvider {…}                       definitionProvider true
typeDefinitionProvider true    implementationProvider true                     referencesProvider {workDoneProgress:true}
documentHighlightProvider true documentSymbolProvider true                     codeActionProvider {…}
codeLensProvider {…}           documentFormattingProvider true                 documentRangeFormattingProvider true
documentOnTypeFormattingProvider {…}  renameProvider {…}                       foldingRangeProvider true
executeCommandProvider {…}     selectionRangeProvider true                     callHierarchyProvider true
semanticTokensProvider {…}     typeHierarchyProvider true                      inlayHintProvider {…}
workspaceSymbolProvider true   workspace {}
serverInfo.name = "CSharpVisualBasicLanguageServerFactory";  _roslyn_processId = <pid>
```

| Needed by the diff | present |
|---|:-:|
| `documentSymbolProvider` | yes |
| `referencesProvider` | yes |
| `callHierarchyProvider` | yes |
| `implementationProvider` | yes |

→ Roslyn stays primary. The client report (claude-code#38683) that `callHierarchyProvider` is missing did
not reproduce on 5.12.0-1.26426.8 with the capabilities above.

### Non-standard init, as actually observed

- `solution/open { solution: file:///C:/Projects/Brain/Shonkor.slnx }` loads `.slnx` — the fallback
  `project/open` was never triggered on Brain; on MuM the `.sln` loaded the same way.
- Readiness = `workspace/projectInitializationComplete`. **It is sent without `params`.** A StreamJsonRpc
  handler that declares a parameter object never binds, the notification is dropped without an error
  and the client waits forever — the first run measured "all projects loaded at 5,5 s, ready never".
  Both shapes are now bound (`LspClient.cs`, `ServerCallbacks.ProjectInitializationComplete`).
- Server→client requests that must be answered or loading stalls: `workspace/configuration` (array of
  `null` per item), `client/registerCapability`, `window/workDoneProgress/create`,
  `workspace/_roslyn_projectNeedsRestore`, `window/_roslyn_showToast` (seen on MuM only).
- The server's own log directory stayed empty at `Information`; everything useful came through
  `window/logMessage` (written to `bench/lsp-diff.log`).

## 3. Load time

Every run spawns a fresh server (no daemon mode). "cold"/"warm" name the run order; the OS/MSBuild
caches are the only thing that differs between them. `t_init` = spawn → `initialize` response,
`t_ready` = spawn → `projectInitializationComplete`, both measured by the client's stopwatch started
immediately before `Process.Start`.

### Brain — `Shonkor.slnx`, 10 projects, net10.0

| run | t_init | t_ready | note |
|---|--:|--:|---|
| very first spawn on this machine (readiness handler still broken) | 1,3 s | 5,5 s ("Abgeschlossenes Laden aller Projekte in 00:00:04.0") | from the server log, not the client |
| cold (first run after the fix) | 1,0 s | 4,5 s | |
| warm | 0,7 s | 4,3 s / 4,2 s | two consecutive runs |
| final cold (graph @ `d1f4c3f`) | 1,4 s | 8,4 s | machine under load from the earlier runs' BuildHosts exiting |
| final warm | 1,4 s | 8,5 s | |

`shonkor index .` on the same checkout (forced full reparse of 362–364 files because the toolchain
fingerprint changed): **15,28 s** and **30,00 s** elapsed on the two indexes performed for this spike
(`src/Shonkor.CLI/Program.cs:295`). `shonkor-bench --search-latency` on the resulting graph (4 005
nodes, 8 876 edges, 23,7 MB): FTS5 median 0,29 ms / p95 1,22 ms / max 7,42 ms; 2-hop CTE median 55,46 ms
/ p95 188,28 ms / max 228,09 ms.

### MuM — `C:\Projects\sitecoreMuM\SitecoreMuM.sln`, 65 `.csproj`, .NET Framework 4.8 (old-style csproj), load-only

| run | t_init | t_ready | projects "erfolgreich abgeschlossen" |
|---|--:|--:|--:|
| cold | 1,4 s | **22,0 s** | 61 of 65 |
| warm | 1,5 s | **17,8 s** | 61 of 65 |

Server log on MuM: one project "weist nicht aufgelöste Abhängigkeiten auf"
(`MuM.Foundation.ErrorHandling`), several `Warning while loading` (processor-architecture conflict
MSIL vs x86 in `MuM.Feature.Newsletter`, `MuM.Deploy.Website`; unreadable
`obj\Debug\*.AssemblyReference.cache` in four projects), then "Wiederherstellung abgeschlossen" and
"Abgeschlossenes (erneutes) Laden aller Projekte in 00:00:20.25". The .NET Framework projects went
through the bundled `BuildHost-net472`. No diff was run on MuM (out of scope).

### `t_warm` — per request after readiness, Brain, ms (final cold / final warm)

| Method | n | median | p95 | max |
|--------|--:|-------:|----:|----:|
| `textDocument/documentSymbol` (first request per file) | 68 | 5,2 / 5,0 | 19,4 / 22,6 | 293 / 288 |
| `textDocument/prepareCallHierarchy` | 93 | 1,9 / 1,8 | 51 / 56 | 3306 / 2984 |
| `callHierarchy/incomingCalls` | 93 | 57 / 48 | 887 / 822 | 1103 / 984 |
| `textDocument/references` | 106 | **518 / 517** | 781 / 797 | 1459 / 1513 |
| `textDocument/implementation` | 58 | 4,0 / 4,0 | 851 / 846 | 955 / 984 |

The first run of the day (before the readiness fix was in place, same requests) had `references` at
514 ms median and `incomingCalls` at 17 ms median — `references` is consistently ~0,5 s per symbol on
this solution; the others are fast with a long tail. The graph answers the same questions from SQLite in
the sub-millisecond to ~200 ms range above.

## 4. The diff — what was compared

- Graph: `shonkor.db` at `indexedRevision d1f4c3f9…` (= solution HEAD), built with the default
  `SHONKOR_SEMANTIC_CSHARP` (on), 4 023 nodes / 8 906 edges. The runner refuses a revision mismatch **and
  a working tree with modified `.cs` files** — an early run with five edited-but-unindexed files produced
  spurious "no-node" gaps in exactly those files, because line containment is only valid when both sides
  read the same text.
- `Reason = SemanticSymbol` edges in the graph: 5 336 (split at the previous index, one commit earlier:
  CALLS 2 233, REFERENCES_TYPE 1 742, INSTANTIATES 881, IMPLEMENTS_MEMBER 363, IMPLEMENTS 101, EXTENDS 2,
  OVERRIDES 0). Three edges carry
  `Reason = Unspecified` — all `DEFINES_COMPONENT` from the TypeScript plugin's XM Cloud component parser,
  none of the relations under test; reported, not blocking (see §7).
- Seeds, mechanical: SemanticSymbol edges grouped by the **source** node's file, top 10 by distinct
  `(source, target, relationship)` pairs, ties ordinal by path. Result (pairs): `McpToolsTests.cs` 153,
  `ParserAndStorageTests.cs` 129, `EditLoopTools.cs` 117, `SqliteGraphStorageProvider.cs` 113,
  `ProvenanceReasonTests.cs` 110, `AnalyzeTools.cs` 99, `ReadTools.cs` 98, `TypeScriptPluginTests.cs` 95,
  `TypeScriptSemanticLinkerTests.cs` 86, `src/Shonkor.CLI/Program.cs` 81 → **1 081 pairs, 274 distinct
  (target, relationship) queries**.
- Oracle per relation, fixed before the first request: `CALLS` → `prepareCallHierarchy` +
  `incomingCalls` at the callee (caller = `from.selectionRange.start`); `REFERENCES_TYPE`/`INSTANTIATES`
  → `references` (`includeDeclaration:false`); `IMPLEMENTS`/`EXTENDS`/`OVERRIDES`/`IMPLEMENTS_MEMBER` →
  `implementation`. IMPLEMENTS/EXTENDS were added during the run: the linker emits them at
  `SemanticSymbol` too, and 26 pairs would otherwise have sat in "contradicted" for want of an oracle.
- Identity: request anchor = `documentSymbol.selectionRange.start` of the symbol whose bare name equals
  the node's name and whose identifier line lies inside `StartLine..EndLine`; answers mapped back by
  **line containment only** at the linker's granularity per relation (`SemanticCsharpLinker.cs:253,280,295-308`),
  file via `Path.GetFullPath(uri.LocalPath)` + `FilePaths.Comparer`. Two members on one line → unmappable.
  Roslyn names property symbols `Name : Type` — stripped, or 30 interface properties anchor nowhere.

## 5. Result — graph pairs against the server

| Relationship | Pairs | Confirmed | Contradicted | Unmappable |
|--------------|------:|----------:|-------------:|-----------:|
| REFERENCES_TYPE | 254 | 225 | **5** | 24 |
| CALLS | 487 | 483 | 0 | 4 |
| INSTANTIATES | 201 | 201 | 0 | 0 |
| IMPLEMENTS | 26 | 26 | 0 | 0 |
| IMPLEMENTS_MEMBER | 113 | 113 | 0 | 0 |
| EXTENDS / OVERRIDES | 0 | — | — | — |

Identical on both runs. 274 targets queried, 246 anchored.

- **Unmappable 28 = 28 dangling targets**, all of one shape: `<referencing file>::System.ValueTuple\`2`
  (and \`3`). `RoslynSemantics.ToNodeId` takes the tuple *type's* first declaring syntax reference, which
  for `(a, b)` is the tuple expression in the referencing file — an id no parser ever creates. A graph
  defect, not an LSP finding (§7).
- **Contradicted 5**, all REFERENCES_TYPE, all of one shape: the source file has **no textual occurrence
  of the target's name**. `Shonkor.CLI.Program → AssemblyPluginLoadResult`, `FindUsagesTool/ReferencesTool/ReasonFilterTests → McpToolHelpers+ReasonFilter`,
  `TypeScriptPluginTests → NodeState`. The linker walks every `TypeSyntax` and `var` is one; its
  `GetSymbolInfo` resolves to the inferred type, so `var x = ReadReasonFilter(args)` yields a
  REFERENCES_TYPE edge. Find-all-references does not count an inferred type as a reference. Semantic
  difference in what "references" means, not an error on either side — but it is a difference a
  consumer will notice.
- INSTANTIATES was checked through `references` on the constructed type, i.e. confirmed means "the source
  references the type", weaker than "instantiates". No reverse gap was computed for it.

## 6. The number that counts — the reverse gap

Server answers for the 246 anchored targets that the graph has **no** edge for (measured against every
graph source of the target, not only the seed files'):

| Relationship | External | Generated | Unmappable | LinkerScope | Implicit | **Other** |
|--------------|---------:|----------:|-----------:|------------:|---------:|----------:|
| REFERENCES_TYPE | 0 | 0 | 117 | 0 | 0 | **26** |
| CALLS | 0 | 0 | 6 | 0 | 194 | **191** |
| IMPLEMENTS | 0 | 0 | 11 | 0 | 0 | **2** |
| IMPLEMENTS_MEMBER | 0 | 0 | 0 | 0 | 0 | **44** |

Buckets: *External* = file outside the solution root; *Generated* = `obj/`, `*.g.cs`, `*.Designer.cs`,
GlobalUsings, AssemblyInfo; *Unmappable* = file indexed, but no node of the relation's granularity at
the line; *LinkerScope* = node exists but the linker never attributes that relation to its kind;
*Implicit* = no call-site line names the callee; *Other* = a node exists at the right granularity and
the edge is missing.

What the buckets contain, checked against the graph DB:

- **Implicit 194 (CALLS)** — 196 of the raw hits target `SqliteGraphStorageProvider.Dispose#0`: `using var
  provider = …` in tests. The call hierarchy counts the implicit disposal; the linker only walks
  `InvocationExpressionSyntax` (`SemanticCsharpLinker.cs:278`). Same class: `foreach`, `await`, operators.
  Not a bug — a scope decision — but a consumer asking "who calls Dispose" gets two different answers.
- **Unmappable 117 (REFERENCES_TYPE) + 6 (CALLS) + 11 (IMPLEMENTS)** — top-level statements
  (`src/Shonkor.Web/Program.cs` 11, `src/Shonkor.Bench/Program.cs` 11: no type or method node exists for
  `<Main>$`), `<see cref="…"/>` in doc comments (the linker does not descend into structured trivia; FAR
  does), file-scoped `delegate` declarations (`McpEndpoints.cs:22`), and interface members referenced
  from `record` primary-constructor parameters. The graph cannot represent these; the server can.
- **Other, CALLS 191** — the real gap. 130 from `tests/Shonkor.Tests`, 53 from `Shonkor.Infrastructure`.
  Top targets: `McpRequestHandler.ProcessJsonRpcMessageAsync#1` ×53, `GetIncidentEdgesAsync#2` ×18,
  `ToHandle#2` ×15, `GetNodeByIdAsync#2` ×15, `Shorten#2` ×12, `ScanFileAsync#2` ×10,
  `SourceText.ReadAsync#2` ×10. Verified example: `tests/Shonkor.Tests/BlastRadiusTests.cs:89`
  `ResultOf(await handler.ProcessJsonRpcMessageAsync(ToolCall(new { … })))` — the graph has 33 CALLS out
  of that file (to the in-file helpers `Affected`, `SetupAsync`, `ToolCall`) and **none** to
  `ProcessJsonRpcMessageAsync`, while 58 CALLS to the same target exist from other test files. The
  invocation resolves for the server and not for the linker's whole-repo compilation
  (`SemanticCsharpLinker.cs:63-89`: every `.cs` in one compilation, TPA references only, no NuGet or
  project boundaries). **Cause not established in the time-box** — candidates are overload resolution
  failing on an argument whose type comes from an unreferenced package, or the duplicated plugin sources
  under `plugins/` colliding in the single compilation. This is the follow-up with the highest value.
- **Other, IMPLEMENTS_MEMBER 44** — 18 from the plugin projects (`Shonkor.Plugin.Sitecore` 12,
  `.Kentico` 3, `.Optimizely` 3: e.g. `KenticoPlugin.SupportedExtensions` implements
  `IFileParser.SupportedExtensions`; the graph has 4 IMPLEMENTS_MEMBER out of `Shonkor.Plugin.Kentico`
  but not this one) and 26 from test doubles. Same suspicion as above: the single compilation resolves
  some interface members and not others.
- **Other, REFERENCES_TYPE 26 / IMPLEMENTS 2** — small; `IGraphView`, `GraphIndexScanner`, `IMcpTool`,
  `IPluginHost` referenced from tests and the TypeScript plugin. Not analysed individually.

Direction confirmed: **LSP ⊇ graph** for every relation except the `var`-inferred REFERENCES_TYPE
edges (5 of 254), which only the graph has.

## 7. Defects this spike surfaced (follow-up candidates, not fixed here)

1. Dangling REFERENCES_TYPE targets `<file>::System.ValueTuple\`N` — 28 edges in the seed set alone;
   `RoslynSemantics.ToNodeId` should treat tuple types as external (metadata-only), not as declared in
   the referencing file.
2. CALLS edges missing for resolvable invocations in the whole-repo compilation (191 in ten seed files'
   targets; `BlastRadiusTests.cs:89` reproduces). Needs the linker's unresolved-symbol reasons logged.
3. IMPLEMENTS_MEMBER missing for plugin-project implementers of `IFileParser` members (18) and test
   doubles (26).
4. `DEFINES_COMPONENT` edges from the TypeScript plugin's XM Cloud component parser carry
   `Reason = Unspecified` (3 on Brain) — the "#428: every producer declares its reason" claim has one
   producer left.
5. `var`-inferred REFERENCES_TYPE: decide whether the graph should keep emitting them (they are
   compiler-true) or mark them so that consumers comparing against find-references are not surprised.

## 8. Open questions for the decision ticket

- **Is 0,5 s per `references` acceptable** for the whole-program questions where the graph currently
  answers in ≤ 200 ms? `incomingCalls`/`implementation` are fast (median ≤ 60 ms) with p95 near 0,9 s.
- **What does "reference" mean for the product** — the linker's (every resolved `TypeSyntax`, `var`
  included) or the server's (identifier occurrences, cref included)? The 5 contradicted and the 117
  unmappable are the two sides of that one question.
- **Implicit calls**: should the graph emit CALLS for `using` disposal / `foreach` / `await` pattern
  members? 194 of 591 server-only locations are this.
- **Load time on a real solution**: MuM readiness is 18–22 s per fresh spawn against ~4–9 s on Brain;
  daemon mode (`--daemon-mode`, keep-alive) was not tried. A per-request server spawn is not viable;
  a long-lived one changes the deployment shape.
- **Not measured**: MSBuild/SDK availability on machines without a matching SDK (the BuildHost
  "reloaded to start from C:\Program Files\dotnet\dotnet.exe to match necessary SDK location"),
  Linux, and the server's memory footprint.

## 9. Reproduce

```
dotnet tool install -g roslyn-language-server --prerelease           # 5.12.0-1.26426.8 at time of writing
dotnet build Shonkor.slnx -c Release
dotnet run --project src/Shonkor.CLI -c Release --no-build -- index . # graph at HEAD, clean tree
dotnet run --project src/Shonkor.Bench -c Release --no-build -- shonkor.db --lsp-diff \
  --lsp "<path-to>\roslyn-language-server.exe --stdio --logLevel Information --extensionLogDirectory <dir>"
# → bench/lsp-diff.md, bench/lsp-diff.json, bench/lsp-diff.log (all git-ignored). Run twice for cold/warm.
# MuM, load time only:
dotnet run --project src/Shonkor.Bench -c Release --no-build -- shonkor.db --lsp-diff --load-only \
  --solution C:\Projects\sitecoreMuM\SitecoreMuM.sln --lsp "<same>"
```

Unit tests for the pure mapping (`tests/Shonkor.Tests/LspDiffTests.cs`, 25 cases) run without a server.
`LspClient` is untested by design — it is the spike's throwaway wire.
