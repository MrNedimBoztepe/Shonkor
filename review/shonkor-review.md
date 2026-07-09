# Shonkor – Critical Review (Precision, Grounding, Stability)

**Status:** 2026-07-08, HEAD `e2031dd` · **Method:** Code review across six subsystems (Ingestion, Graph construction, Embedding/Retrieval, Answer grounding, MCP server, .NET/Eval); all findings verified in source code, evidence given as `File:Line`.

---

## Executive Summary

Shonkor is substantially better built than the typical RAG prototype: a clean async/SQLite layer, a well-considered drift/freshness model, embedding versioning, token-efficient MCP outputs, and an unusually honest benchmark culture. **But the core promise "precise and grounded" is currently not delivered in three places — and at none of them is it backed by measurement:**

1. **"Precise" is unproven.** There is **no answer-groundedness eval**: one existed (citation validity, abstention recall in `Shonkor.Eval`) and was removed without replacement in commit `009b8d7`. The retrieval golden set is **circular** for the vector retriever (query = doc comment of the target symbol, which appears verbatim in the embedding document — P@1 0.88 is an upper bound, not a measurement), the +21-pp RAG comparison measures coverage **asymmetrically** (Shonkor's coverage against the pre-budget subgraph, the baseline against delivered chunks), and the default mode `search_hybrid` is **never** benchmarked.
2. **The corpus is amputated at the root.** Method/constructor bodies are silently truncated to **500 characters** during parsing (`RoslynAstParser.cs:462-470`) — FTS, embeddings, and even `get_source` (prefers the stored body, `ReadTools.cs:91-94`) operate on the first half of the method. Markdown sections have **no** content/line range at all — documentation is effectively not chunked.
3. **Grounding is a prompt paragraph without enforcement.** No overall token budget and no `num_ctx` → Ollama truncates silently, and **the grounding/citation/abstention rules at the start of the prompt drop out first** — precisely in the context-rich case. Citations are never validated against the source labels; there is no relevance threshold, weak retrieval still leads to an LLM answer.

On top of that come two silent correctness time bombs: `INSERT OR REPLACE` corrupts the external-content **FTS5 index** (delete triggers do not fire without `recursive_triggers`), after which *every* search silently degrades into an unordered `LIKE` scan — and **provenance integrity** (the stated core differentiator) is violated by two write paths that store heuristic or LLM edges as `Extracted`.

**Good news:** The five critical points are mostly small, local fixes (one `ON CONFLICT` upsert, one `num_ctx` + budget, one truncation constant, two provenance stamps); only the eval is real build-out work — and the harness infrastructure for it (`Shonkor.Bench`, baseline gate) already exists. Prioritized implementation: [roadmap.md](roadmap.md), measures: [improvements.md](improvements.md), eval build-out: [eval-plan.md](eval-plan.md), tickets: `tickets/TICKET-201…215`.

---

## Findings

### CRITICAL

**K1 — There is no measurement of answer precision; the existing retrieval numbers are methodically skewed.**
Finding: `src/Shonkor.Eval/` contains only `bin`/`obj` without git history; the groundedness eval (citation validity, must-cite rate, abstention recall, introduced in `ccd6c22`) was removed without replacement in `009b8d7` ("unify Benchmarks + Eval") — the commit message says so itself. `bench/golden/doc-intent.json` is generated from `<summary>` comments (`GoldenSetGenerator.cs:38,50-53`) that, via `EmbeddingTextBuilder.Build` (`EmbeddingTextBuilder.cs:40,52`), also appear in the embedding document → the vector query is a near-substring of the target document. `RagBaselineBenchmark.cs:79-83` checks Shonkor's coverage against the **pre-budget** subgraph, the baseline against delivered chunks. `search_hybrid` (default in the dashboard "Brain" mode and MCP) appears in no benchmark. The gate metric is, of all things, P@k with k=10 given exactly 1 relevant (maximum 0.1; tolerance 0.03 = 31% relative drop required, `Shonkor.Bench/Program.cs:157-172`); no CI wiring (`.github/workflows/ci.yml` only builds/tests).
Impact: All precision claims in the README (incl. "guarantees 100% precise") are assertions. Regressions in retrieval or answer quality are invisible.
→ Fix: [eval-plan.md](eval-plan.md), TICKET-201/202.

**K2 — Method bodies are truncated to 500 characters during parsing; `get_source` preferentially serves the amputation.**
Finding: `RoslynAstParser.cs:462-470` (`GetTruncatedContent`, applied to Method :161 and Constructor :232). `EmbeddingTextBuilder` (MaxBodyChars 1500, head+tail design from TICKET-105) never sees more than these 500 characters — the tail window is dead for C#. `ReadTools.cs:91-94` prefers the stored body over the file slice.
Impact: Queries on the second half of the method (error handling, return paths) can hit via neither FTS nor vector; agents silently receive amputated code as a supposedly complete source — direct grounding damage in the core corpus (C#).
→ TICKET-204.

**K3 — `INSERT OR REPLACE` corrupts the external-content FTS5 index; afterwards every search silently degrades to unordered `LIKE`.**
Finding: `SqliteGraphStorageProvider.cs:104` (`INSERT OR REPLACE INTO Nodes`), FTS as external-content table with AFTER triggers (`SqliteSchema.cs:112-150`), `PRAGMA recursive_triggers` set nowhere → the implicit DELETE of the REPLACE does not fire the delete trigger; the FTS index retains ghost postings under orphaned rowids (a documented SQLite failure mode up to `SQLITE_CORRUPT_VTAB`). Exposed via `CrossTechLinker.cs:203` (virtual nodes re-upserted on every scan), `McpToolContext.cs:223` (`record`), `StatsEndpoints.cs:109`. Since `SearchAsync` catches **every** `SqliteException` and falls back to `LIKE '%q%'` without `ORDER BY` with score 1.0 (`:288-319`), the degradation is invisible; the count-diff rebuild (`SqliteSchema.cs:157-162`) only heals at the next process start and only when the row count happens to differ.
Impact: Stale hits and duplicates in FTS, then complete ranking loss — unnoticed in the long-running process (Web + MCP).
→ TICKET-203 (Fix: `INSERT … ON CONFLICT(Id) DO UPDATE` — fires the UPDATE trigger, preserves rowid, and incidentally stops the unnecessary resetting of `NeedsSemanticAnalysis`/embedding on re-upserts).

**K4 — No overall prompt budget, no `num_ctx`: Ollama truncates silently, and the grounding rules drop out first.**
Finding: `BuildRagPrompt` (`OllamaSemanticAnalyzer.cs:158-196`) truncates only per node (2,000 characters); the UI sends up to 10 nodes + 6 chat turns (`app.js:255,313-316`) ≈ 6-8k tokens. `num_ctx` is never set (0 hits in `src`), Ollama default often 2048-4096. The instruction block (exclusivity, abstention, citation obligation, injection fence) is **at the start** — tail-retention truncation discards it first.
Impact: Precisely in the context-rich case the model generates freely over raw code, without the server or user noticing — the single largest hallucination risk in the answer path.
→ TICKET-205.

**K5 — Provenance integrity (core differentiator) is violated by two write paths.**
Finding: (a) The name-based fallback of `SemanticCsharpLinker` (heuristic, multi-candidate: `SemanticCsharpLinker.cs:168-175`) persists edges without provenance (`:120-122`) → default `Extracted` (`GraphEdge.cs:20`); the stamp enforcement point (`GraphIndexScanner.cs:679-680`) only covers parser edges. (b) LLM-generated `RELATES_TO` concept edges are inserted via raw SQL without a provenance column (`SqliteGraphStorageProvider.cs:1443-1445`, column default 0 = Extracted). Additionally, the regex parsers `GraphQLParser` and `SitecoreXmCloudPlugin` claim Extracted for lack of a `DefaultProvenance` override (`IFileParser.cs:37`).
Impact: Consumers that filter/rank by provenance (`references provenance=extracted`, capsule, blast radius) treat heuristics and LLM guesses as compiler facts — the trust model with which Shonkor differentiates itself from vector RAG is hollow at exactly these points.
→ TICKET-207.

### HIGH

**H1 — FTS5 throws syntax errors on normal code queries → unordered LIKE fallback as "ranking".** `Foo.Bar`, `nomic-embed-text`, `List<T>` are FTS5 syntax (`SqliteGraphStorageProvider.cs:265-319`); the fallback has no ORDER BY, score uniformly 1.0, `%`/`_` unescaped; `search_hybrid` feeds these pseudo-ranks into RRF (`FindTools.cs:248,267`). Fix: sanitize tokens into phrase quotes instead of falling back. → TICKET-203.

**H2 — Citations are never validated.** The entire mechanism is a prompt request (`OllamaSemanticAnalyzer.cs:163-168,184`); model text goes out verbatim. Fabricated or mis-attributed `[Name @ file:lines]` labels are indistinguishable from real ones to the user. Fix: label-set validation + flagging, clickable links. → TICKET-206.

**H3 — No relevance threshold before the LLM call.** Context = top 10 of the last search, score is discarded (`app.js:255-257`); the server calls the LLM for every resolvable NodeId set (`SearchEndpoints.cs:114-141`). Abstention hangs solely on the instruction-following of a ~7B model. `search_semantic` also has no similarity floor and hides scores in the MCP output (`FindTools.cs:197-202`). → TICKET-206.

**H4 — Incremental relink destroys the edges that `implementations_of` relies on.** The tool queries only name-target edges (`AnalyzeTools.cs:357-360`), relink deletes IMPLEMENTS/EXTENDS of both forms (including for never-re-parsed referencer files, `GraphIndexScanner.cs:484-489`) and re-emits only id-target (`SemanticCsharpLinker.cs:36,105-111,206-219`). Every `reindex_file` further erodes the answer to "who implements IFileParser?" — cumulative recall loss in a headline tool. → TICKET-213.

**H5 — Name-target phantom nodes act as traversal hubs; JS imports effectively never connect.** Roslyn base-type edges target display strings (`IRepository<Foo>`, `RoslynAstParser.cs:345-356`) with an "I + capital letter" heuristic as Extracted; the subgraph CTE expands over non-existent endpoints (`SqliteGraphStorageProvider.cs:471-482`) → all bases with the same name across namespaces are 2 hops from each other. JS: `IMPORTS` targets without extension resolution never match the `.tsx` node ids (`JavaScriptParser.cs:114-142`) — the intra-project JS graph is empty, package names (`react`) act as phantom hubs. → TICKET-213.

**H6 — Markdown is not chunked.** `MarkdownSection` nodes have neither content nor StartLine/EndLine (`MarkdownHierarchyParser.cs:76-87`); their embedding text is just the title; documentation retrieval works only at file level (head+tail 1500 characters, cap 100k without marker). Section citations impossible. → TICKET-211.

**H7 — Line numbers inconsistent: C# 0-based, plugins 1-based, docs say 1-based.** `GraphNode.cs:20-24` vs `RoslynAstParser.cs:128,163,198,234,329` (Roslyn `.Line` is 0-based) vs `PythonParser.cs:61`; `TryReadSourceSlice` assumes 0-based (`McpToolContext.cs:119-123`). Every C# citation (`locate`, `outline`, `edit_plan` checklists) shows a line one too high; plugin nodes slice incorrectly. → TICKET-208.

**H8 — `set_project` over the HTTP relay reports success but has no effect.** A new handler/context is created per POST (`McpEndpoints.cs:68-70`); the session override (`MetaTools.cs:118`) dies with the request; the stdio proxy sends each line as its own POST. The agent then transparently operates on the wrong project graph — confident-lie semantics. → TICKET-209.

**H9 — No project-root containment in file tools (path traversal by design).** Absolute paths and `@/../` handles pass through unchecked (`EditLoopTools.cs:46-50`, `McpToolHelpers.cs:174-180`, same pattern in `check_edit`, `outline`, `freshness`, `review`); `reindex_file` indexes arbitrary files, `search_graph`/`get_source` exfiltrate them. The SaaS path is protected (tenant-locked without parser), locally + `Security:AllowLocalBypass` (`ApiKeyMiddleware.cs:30-43`) this becomes an unauthenticated arbitrary file read. → TICKET-209.

**H10 — MCP `generate_capsule` ignores the budget synthesizer.** Seeds FTS-only, legacy overload without seed protection, blind tail truncation at the last `##` (`ReadTools.cs:317-335`, `McpToolHelpers.cs:186-202`); the alphabetical file grouping of the unlimited renderer (`ContextCapsuleSynthesizer.cs:169`) cuts away exactly the relevant seeds under truncation — while the web endpoints do it correctly (`SearchEndpoints.cs:359-360`). → TICKET-214.

**H11 — Embedding lifecycle broken in the edit loop.** A one-line edit discards summaries/embeddings for the entire file (no per-node hash, `GraphIndexScanner.cs:612-613` + `SqliteGraphStorageProvider.cs:147-149`); the stdio MCP host never re-embeds (only the web worker does) → edited files silently drop out of `search_semantic`; the CLI, conversely, re-embeds **everything** on every `--embed` run (`Program.cs:348-349`, no `Embedding IS NULL` filter). On top of that: an enrichment race stamps stale embeddings onto fresh content (`SqliteGraphStorageProvider.cs:1476-1492` without a content guard) and the queue can jam forever on 16 permanently failing nodes (`:1385-1394` without ordering/attempt tracking). → TICKET-212.

**H12 — Vector search is a full-table blob scan per query.** `SELECT Id, Embedding FROM Nodes …` + per-row alloc (`SqliteGraphStorageProvider.cs:373-405`); at 100k+ nodes ~300 MB I/O and 100k allocations per query, `search_hybrid` doubles that. Correctness is fine (exact top-K heap), scaling is not. → TICKET-215.

**H13 — MCP tool errors as JSON-RPC protocol errors instead of `isError:true` result.** `McpRequestHandler.cs:191-195` → `-32603`; clients hide the actionable message from the model (spec: execution errors belong in the result). Additionally `ping` not implemented (spec: MUST), parse error without `-32700` response, protocol version echoed unchecked (`:133-136`). → TICKET-210.

**H14 — Chat-history laundering bypasses the injection fence.** Earlier assistant answers (derived from untrusted context) land via `composedQuery` in the trusted `NUTZERFRAGE` slot (`app.js:312-316`) — a two-stage injection past the "context is data" fence. The existing injection detector (`SuspiciousContentPostProcessor.cs`) is completely decoupled from the answer path. → TICKET-206.

### MEDIUM

- **M1** nomic task prefixes default off — and the A/B test that came out "within noise" embedded queries with the **document** prefix (`RetrievalBenchmark.cs:68` → kind-less overload, `OllamaEmbeddingService.cs:42-43`), so it never measured the production asymmetry; a prefix change triggers no re-embed (`MarkStaleEmbeddingsForReembedAsync` checks only dim/model). → TICKET-202/212.
- **M2** No atomicity across delete→Nodes→Edges (three separate transactions, `GraphIndexScanner.cs:199-217`): a crash after the node commit leaves a hash-valid, edgeless file graph that the hash skip never repairs. → TICKET-212.
- **M3** Plugins create conflicting file nodes: `DockerPlugin.cs:466-476` non-deterministically clobbers the scanner's ContentHash (permanent re-index), `PythonParser.cs:29-37` creates a second file identity. → TICKET-212.
- **M4** Node id scheme: generics arity missing (collision `Foo`/`Foo<T>`), partials split on arbitrary parts, overload-span ids churn on edits above them; relink via `TypeReferences` (type names only) misses chained calls/using-static → dangling CALLS edges. → TICKET-213.
- **M5** `GraphEdge.Properties` is never persisted (4-column table, `SqliteSchema.cs:60-68`); `INSERT OR IGNORE` freezes stale provenance. → TICKET-207.
- **M6** Traversal without hub damping/provenance filter; `find_path` routes over `BELONGS_TO_MODULE`/`RELATES_TO` hubs; the OR join in the CTE prevents both edge indexes (full scan per level). → TICKET-213/215.
- **M7** Unbounded MCP outputs: `limit`, `hops`, `maxHops` unclamped, `maxChars` default unlimited (`FindTools.cs:43,125`, `ReadTools.cs:106,221,250-255,329`, `AnalyzeTools.cs:473`) — in contrast to the otherwise disciplined clamping practice. → TICKET-210.
- **M8** FTS does not index `Summary` (`SqliteSchema.cs:114-118`); concept nodes are never embedded (`SqliteGraphStorageProvider.cs:1392,1432-1433`) — the concept layer is invisible to search. → TICKET-211.
- **M9** The `signature` property is written by no parser, although `EmbeddingTextBuilder.cs:39` reads it; class nodes have no content → class vector ≈ name only on CLI databases. → TICKET-204.
- **M10** Retry hygiene: both Ollama services retry cancellation and deterministic 4xx (`OllamaEmbeddingService.cs:87-96`, `OllamaSemanticAnalyzer.cs:128-137`); blocking RAG retries the **full** generation 3× (up to ~6 min); the webhook background scan captures the request token (`WebhookEndpoints.cs:241-245`) — push indexing can die silently. → TICKET-210 (appendix) / improvements.
- **M11** `Security:AllowLocalBypass=true` disables auth completely behind a reverse proxy (`ApiKeyMiddleware.cs:26-43`); combined with H9. → TICKET-209.
- **M12** `record` memory: unbounded length, edges to unverified `connectedNodeIds` (`McpToolContext.cs:226-241`), a persistent cross-session injection channel; indexed content in tool outputs marked as data nowhere. → TICKET-209.
- **M13** Answer language hardcoded German (`OllamaSemanticAnalyzer.cs:183,194`) with an EN/TR UI — raises the instruction load of the small model and depresses compliance with the remaining rules. → TICKET-206.
- **M14** `GetAllNodesAsync` (incl. content) on request paths (`InsightsEndpoints.cs:32,74`, `MemoryAndStatsTools.cs:80,139,204`) — full load on large graphs per click; `architecture` makes up to 3,000 sequential edge queries. → improvements (V13).
- **M15** XmCloud components name-keyed across file boundaries (collision in monorepos, `SitecoreXmCloudPlugin.cs:233`); the CrossTechLinker `NormalizeName` strips "controller" everywhere (`ControllerFactory`→`factory`, `CrossTechLinker.cs:47-52`); multi-candidate should be `Ambiguous`. → TICKET-213.

### LOW (selection)

FTS tie order without a secondary key; no `seed` at temperature 0, `AnalyzeNodeAsync` with no temperature at all (non-deterministic summaries in the RAG prompt); a mid-stream error marker lands as assistant text in the chat history; `/api/ask` without NodeIds cap/dedup; no `IndexedAt` timestamps; stale cleanup with a raw path prefix (`App` matches `App2`); JS ids lowercased vs. original-case file nodes (dangling `DEFINED_IN` edges on Windows checkouts); relationship vocabulary as magic strings in ~20 files; RRF without source weighting; `rag-chunk-cache.json` does not cache per embedding model; German truncation marker in English embedding documents; bench match via `Id.Contains` (substring inflation); exception messages in `-32603` can leak paths.

---

## What holds up (verified, one sentence each)

- **SQLite layer:** Connection-per-op + WAL + busy_timeout, parameter chunking, batch edge loading, injection-safe parameterization, a correct cycle-safe CTE with deterministic `MIN(Depth)` — clearly above hobby level.
- **Async/streaming:** Zero occurrences of `.Result`/`.Wait()`/`async void` in `src`; clean `IAsyncEnumerable` NDJSON streaming with a truncation marker and a deliberate no-retry after the first byte.
- **Embedding versioning:** Dim+model stamped per vector, stale reconcile at startup, query-time dimension guard — exactly what most RAG systems get wrong.
- **Drift/freshness model:** Hash skip, `DetectDriftAsync`, git-aware reconcile, `TypeReferences` reverse index, stale warnings on tool outputs, id-scheme versioning via `user_version`.
- **MCP token efficiency & tenant isolation:** compact defaults + opt-in verbose, `@/` handles, provenance tags per edge line, honest empty results; on the SaaS side an unforgeable tenant via `HttpContext.Items`, constant-time key comparison.
- **Enrichment worker:** per-cycle DI scope (handler rotation), a real circuit breaker with exponential backoff, linked-CTS sibling cancellation, live config.
- **Benchmark honesty culture:** a fair same-subgraph baseline, matched-token comparison, CIs, reproducible commands — the measurement errors (K1) are bugs, not spin.
