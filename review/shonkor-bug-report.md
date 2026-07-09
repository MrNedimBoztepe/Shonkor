# Bug-Hunt Report: Shonkor (Precise Graph-RAG with MCP Binding)

**Date:** 2026-07-07 · **Scope:** entire repo (`src/`, scope fields were empty) · **Status:** `main` @ `e2031dd`

Methodology: Six parallel deep reads per subsystem (Retrieval/Embedding, Graph, MCP layer, Storage, LLM integration, Parser/Ingestion), followed by manual verification of the critical findings in source code. All findings marked **Confirmed** are backed by concrete code; **Suspected** means: the logic supports it, but runtime/data verification is pending. One agent finding was **refuted** during verification and is not included (see "Checked and clean").

---

## Executive Summary

| Severity | Count |
|---|---|
| Critical | 3 |
| High | 12 |
| Medium | 20 |
| Low | 19 |

**The 3 most dangerous findings:**

1. **BUG-001 — One NaN score poisons the semantic search; two NaN hang the request forever.** A single null vector in the DB silently turns the top-K ranking into "first N rows in table order"; a second null vector produces an infinite loop (`Math.BitIncrement(NaN)` = NaN) and pins the thread.
2. **BUG-002 — FTS5 index corruption on every re-index.** `INSERT OR REPLACE` + external-content FTS triggers without `PRAGMA recursive_triggers`: every upsert of an existing node leaves a ghost FTS entry. Full-text search delivers increasingly wrong/missing hits during operation — with rowid reuse even the **wrong node**.
3. **BUG-003 — An unknown project name silently falls back to the active project.** A typo in `projectName`/the header → answers (and `record` writes!) land in the wrong project graph. In the SaaS case (project deleted/renamed between auth and query) this is a cross-tenant data leak.

A recurring root pattern: **`INSERT OR REPLACE` in `UpsertNodesAsync`** is the common root of BUG-002, BUG-007, and parts of BUG-016 — a rework to `ON CONFLICT(Id) DO UPDATE` fixes three finding groups with one change.

---

## Critical

### BUG-001 — NaN poisons the top-K heap of the semantic search; second NaN → infinite loop
- **Severity:** Critical · **Status:** Confirmed (logic; the trigger needs a null-norm vector in the DB)
- **Location:** [SqliteGraphStorageProvider.cs:390-404](../src/Shonkor.Infrastructure/Storage/SqliteGraphStorageProvider.cs) (`SearchSemanticAsync`)
- **Description:** `TensorPrimitives.CosineSimilarity` returns **NaN** for a null vector (0/0); there is no NaN guard. `Comparer<double>` sorts NaN below everything → NaN becomes `heap.Keys[0]` and is never evictable (eviction only happens on insert). Once the heap is full, `score > NaN` is `false` for **every** real score → all further rows are discarded. On a **second** NaN: `while (heap.ContainsKey(key)) key = Math.BitIncrement(key)` — `BitIncrement(NaN)` stays NaN → infinite loop.
- **Trigger:** a corrupt embedding blob or a model that returns a null vector (e.g. for empty/whitespace text).
- **Impact:** Semantic and hybrid search silently return random results (table order instead of ranking); in the hang case the request thread is occupied forever → thread-pool starvation of the whole server under load.
- **Reproduction:** upsert a node with embedding = `new float[768]` (all 0), run `search_semantic` with `limit` small enough that the heap fills; add a second null-vector node → the request never returns.
- **Fix:** directly after line 390: `if (double.IsNaN(score)) continue;` — additionally reject null-norm vectors on write.

### BUG-002 — FTS5 index corruption: `INSERT OR REPLACE` + external-content FTS without `recursive_triggers`
- **Severity:** Critical · **Status:** Confirmed
- **Location:** [SqliteGraphStorageProvider.cs:104](../src/Shonkor.Infrastructure/Storage/SqliteGraphStorageProvider.cs) (`UpsertNodesAsync`); triggers in [SqliteSchema.cs:112-150](../src/Shonkor.Infrastructure/Storage/SqliteSchema.cs); `recursive_triggers` is set nowhere (checked repo-wide)
- **Description:** On `INSERT OR REPLACE` SQLite deletes the colliding row **without** firing the `AFTER DELETE` trigger (that only happens with `PRAGMA recursive_triggers = ON`). The `AFTER INSERT` trigger fires for the new row (new rowid). Result per re-upsert of an existing node: one orphaned FTS entry (old rowid, old content) + one new entry.
- **Trigger:** any incremental re-index of an already indexed file (`reindex_file`, drift reconcile, another scan).
- **Impact:** `NodesFts` is an external-content table (`content=Nodes`) — ghost entries read column values via the stale rowid. If the rowid is reused for a **different** node, the OLD search text matches the WRONG node; otherwise hits disappear silently. The count-based rebuild guard ([SqliteSchema.cs:157-162](../src/Shonkor.Infrastructure/Storage/SqliteSchema.cs)) only repairs at the **next process start** — a long-running server delivers increasingly wrong full-text results for the entire session.
- **Reproduction:** index a file, edit it, `reindex_file`; then compare `SELECT COUNT(*) FROM Nodes` vs. `SELECT COUNT(*) FROM NodesFts` → the FTS count is higher.
- **Fix:** replace `INSERT OR REPLACE` with `INSERT ... ON CONFLICT(Id) DO UPDATE SET ...` (preserves rowid, fires the UPDATE trigger). Alternatively/additionally `PRAGMA recursive_triggers = ON` in `OpenConnectionAsync`. The `ON CONFLICT` rework also fixes BUG-007.

### BUG-003 — An unknown project name silently falls back to the active project (cross-project/cross-tenant leak)
- **Severity:** Critical · **Status:** Confirmed
- **Location:** [ProjectManager.cs:309-323](../src/Shonkor.Infrastructure/Services/ProjectManager.cs) (`ResolveProject`, line 320: `project ??= GetActiveProject();`); consumer [McpToolContext.cs:75-81](../src/Shonkor.Infrastructure/Services/Mcp/McpToolContext.cs)
- **Description:** If a project name is explicitly requested but not found, there is no error — the **active** project is silently used. The active project is a globally persisted setting changeable via the web dashboard.
- **Trigger:** a typo in `projectName`/`X-Project-Name`; project deleted/renamed during a session; a dashboard user switches the active project while another client makes MCP requests without a project binding.
- **Impact:** Answers come unnoticed from the **wrong graph** (a first-order hallucination source for the consuming agent); `record` writes pollute foreign project graphs. SaaS worst case: a tenant-bound session whose project disappears from the registry between auth and query is redirected to the active project of **another tenant** → data leak.
- **Reproduction:** `tools/call` with `projectName="Doesnotexist"` → the result comes from the active project, without a warning.
- **Fix:** If a name was explicitly requested (or the session is tenant-locked) and resolution fails → JSON-RPC error, never a fallback.

---

## High

### BUG-004 — All C# citations off by one line (0-based StartLine emitted as 1-based)
- **Severity:** High · **Status:** Confirmed
- **Location:** [RoslynAstParser.cs:128,163,198,234,329](../src/Shonkor.Core/Services/RoslynAstParser.cs) (`StartLinePosition.Line`, stored 0-based); output sites print raw: among others [ReadTools.cs:47](../src/Shonkor.Infrastructure/Services/Mcp/Tools/ReadTools.cs), FindTools, AnalyzeTools, EditLoopTools, CLI. [CSharpDiagnostics.cs:90](../src/Shonkor.Infrastructure/Services/CSharpDiagnostics.cs), by contrast, explicitly computes `+ 1 // 1-based for humans/agents` — so the intended convention is 1-based. Only [McpToolContext.cs:109-123](../src/Shonkor.Infrastructure/Services/Mcp/McpToolContext.cs) (`TryReadSourceSlice`) correctly treats the values as 0-based.
- **Impact:** Every `file:line` reference the system emits for C# symbols (`locate`, `find_usages`, `edit_plan`, `signature`, …) shows a line **above** the real declaration — this is the core currency of a "precise" graph-RAG.
- **Reproduction:** call `signature` for a known class and compare the emitted line with the file.
- **Fix:** Fix a convention (recommended: store 1-based, `Line + 1` in the parsers, switch `TryReadSourceSlice` to `-1`), document it on `GraphNode.StartLine`. Also affects EndLine.

### BUG-005 — `INSERT OR REPLACE` wipes embedding versioning and enrichment on every re-upsert
- **Severity:** High · **Status:** Confirmed
- **Location:** [SqliteGraphStorageProvider.cs:104-105,147-149](../src/Shonkor.Infrastructure/Storage/SqliteGraphStorageProvider.cs) vs. [1509-1517](../src/Shonkor.Infrastructure/Storage/SqliteGraphStorageProvider.cs)
- **Description:** The REPLACE column list contains neither `EmbeddingDim` nor `EmbeddingModel` → both become `NULL` on every upsert. `MarkStaleEmbeddingsForReembedAsync` deliberately skips rows with `EmbeddingDim IS NULL` — surviving embeddings are **invisible** to the stale detector; a same-dimension model switch then silently mixes vectors of two models (cosine scores across model boundaries are meaningless). Additionally: `pNeedsAnalysis.Value = 1` is hardcoded (line 148) and `Summary`/`Embedding` are overwritten with the (typically empty) values of the incoming node → every re-index of an unchanged symbol throws away paid LLM enrichment and re-queues it.
- **Aggravation:** `POST /api/interactions/status` ([StatsEndpoints.cs:96-109](../src/Shonkor.Web/Endpoints/StatsEndpoints.cs)) loads a node via `GetNodeByIdAsync` (the mapper reads **no** embedding, [SqliteRowMapper.cs:51-63](../src/Shonkor.Infrastructure/Storage/SqliteRowMapper.cs)) and upserts it back → the node's embedding is destroyed; the endpoint accepts **arbitrary** node IDs.
- **Fix:** `ON CONFLICT(Id) DO UPDATE` that preserves `Summary`/`Embedding`/`EmbeddingDim`/`EmbeddingModel` when the incoming node does not supply them; `NeedsSemanticAnalysis = 1` only on a changed `ContentHash`.

### BUG-006 — `set_project` is a silent no-op over the HTTP relay but reports success
- **Severity:** High · **Status:** Confirmed
- **Location:** [McpEndpoints.cs:68-70](../src/Shonkor.Web/Endpoints/McpEndpoints.cs) (a new `McpRequestHandler` **per POST**); [MetaTools.cs:117-121](../src/Shonkor.Infrastructure/Services/Mcp/Tools/MetaTools.cs) sets `ctx.SessionProjectOverride` on the request-local context
- **Impact:** The client believes it has switched project ("Active project … is now 'X'"), but the next request resolves again via header/active project → the agent reads and **writes** (`record`) confidently into the wrong graph. Combined with BUG-003 doubly treacherous.
- **Fix:** Without a persistent session return a clear error message ("not supported over the HTTP relay — pass `projectName` per call") or persist sessions via an `Mcp-Session-Id` header.

### BUG-007 — Stale-file cleanup deletes data of foreign directories via a path prefix
- **Severity:** High · **Status:** Confirmed
- **Location:** [GraphIndexScanner.cs:190](../src/Shonkor.Infrastructure/Services/GraphIndexScanner.cs) and [:379-380](../src/Shonkor.Infrastructure/Services/GraphIndexScanner.cs)
- **Description:** `indexedFile.StartsWith(directoryPath, OrdinalIgnoreCase)` without a trailing-separator guard: a scan of `C:\Repo` classifies indexed files under `C:\Repo2\…` as "under this directory"; since they are not candidates, they are **deleted from the graph**. Conversely, `directoryPath` is never normalized with `Path.GetFullPath` (candidates are) — a relative/differently-formed path silently disables the cleanup (stale nodes survive every re-index).
- **Trigger:** a DB with more than one root (multi-root, `reindex_file` on an out-of-root path, name-prefix siblings like `Brain`/`Brainstorm`).
- **Fix:** normalize `directoryPath` + append `Path.DirectorySeparatorChar`, then compare.

### BUG-008 — Plugin registry is completely emptied on a transient read error
- **Severity:** High · **Status:** Confirmed
- **Location:** [PluginRegistry.cs:261-273](../src/Shonkor.Infrastructure/Services/PluginRegistry.cs) (`catch { return new List<InstalledPlugin>(); }`), write path `InstallFromZip` (line 133-134), non-atomic `Save()` (line 278)
- **Description:** `Load()` swallows **all** exceptions and returns an empty list. If `registry.json` is currently locked (AV, backup, a second instance — `AssemblyPluginLoader` builds its **own** registry instance with its own lock, [AssemblyPluginLoader.cs:71](../src/Shonkor.Infrastructure/Services/AssemblyPluginLoader.cs)), the next write persists the empty state → **all installed plugins are deleted from the registry**.
- **Fix:** distinguish "file missing" from "read failed" (the latter → abort the operation); write atomically (temp file + `File.Replace`); a shared registry instance or a cross-process mutex.

### BUG-009 — MCP proxy swallows transport exceptions → host hangs indefinitely
- **Severity:** High · **Status:** Confirmed
- **Location:** [McpProxyClient.cs:162-165](../src/Shonkor.CLI/McpProxyClient.cs) (the `catch` only writes to stderr, no JSON-RPC response); the default `HttpClient.Timeout` of 100 s hits slow tools (`audit`, `generate_capsule`)
- **Impact:** The MCP host waits forever for the response to the request id — typical clients thereby block the entire conversation. The non-2xx branch (lines 139-159) does it correctly.
- **Fix:** in the `catch` parse the `id` of the line and write a `-32603` error response to stdout; make the timeout configurably higher.

### BUG-010 — A 64-hex token is misclassified as "already hashed": plaintext storage + permanent auth lockout
- **Severity:** High · **Status:** Confirmed
- **Location:** [TokenHasher.cs:20-25](../src/Shonkor.Infrastructure/Services/TokenHasher.cs) (`LooksHashed`/`EnsureHashed`); caller [ProjectManager.cs:195-201](../src/Shonkor.Infrastructure/Services/ProjectManager.cs)
- **Description:** A **caller-supplied** API key that happens to be exactly 64 hex characters long (the most common form of a 32-byte token) is passed through as a digest and stored in plaintext in `projects.json`. `Verify` hashes the presented token → `SHA256(token) != token` → authentication for this key **always** fails, with no explanatory error. Side finding: `LooksHashed` accepts uppercase hex, `Hash` emits lowercase → an uppercase digest never matches in `Verify` (no `ToLowerInvariant` before the comparison).
- **Fix:** don't guess — store hashes self-describing (`sha256:<hex>`) or a one-time/versioned migration instead of shape-sniffing; normalize `storedHash`.

### BUG-011 — Streaming answers are hard-aborted after 2 minutes, without an incompleteness marker
- **Severity:** High · **Status:** Confirmed
- **Location:** [OllamaSemanticAnalyzer.cs:34](../src/Shonkor.Infrastructure/Services/OllamaSemanticAnalyzer.cs) (`_httpClient.Timeout = TimeSpan.FromMinutes(2)`), stream loop lines 269-322
- **Description:** `HttpClient.Timeout` also applies to reading the response body — even with `ResponseHeadersRead`. A RAG generation > 120 s (a large capsule context window + a 7B model on CPU is enough for that) is aborted mid-stream; the abort arrives as a `TaskCanceledException` from `ReadLineAsync`, and the graceful truncation marker (`[Antwort unvollständig]`, lines 317-322) only fires on `line == null`. In `SearchEndpoints.cs:186-189` partial tokens are already flushed to the client → the answer ends mid-sentence, without a marker.
- **Fix:** `Timeout = Timeout.InfiniteTimeSpan` on the typed client, connect/first-byte timeout via `SocketsHttpHandler.ConnectTimeout`/CTS; rebuild the read loop so the exception path also emits the marker.

### BUG-012 — JS/GraphQL parsers create lowercase node IDs → whole edge families dangle
- **Severity:** High · **Status:** Confirmed
- **Location:** [JavaScriptParser.cs:48,119](../src/Shonkor.Core/Services/JavaScriptParser.cs); [GraphQLParser.cs:48](../src/Shonkor.Core/Services/GraphQLParser.cs) (`filePath.ToLowerInvariant()`); the scanner file node uses original case ([GraphIndexScanner.cs:169](../src/Shonkor.Infrastructure/Services/GraphIndexScanner.cs)); `Nodes.Id` is case-sensitive
- **Impact:** On typical Windows paths (`C:\Projects\…`) **all** `IMPORTS` and `DEFINED_IN` edges point to IDs that no node has → the JS/GraphQL subgraphs are structurally dead. On fully lowercase paths (Linux convention) the component ID instead collides with the file-node ID; who wins is non-deterministic due to `ConcurrentBag` order — if the file node loses, its `ContentHash` is gone and the file is re-indexed on **every** scan.
- **Related (Medium):** relative imports are taken over without extension/index resolution (`./Button` ≠ `Button.tsx`, [JavaScriptParser.cs:133-142](../src/Shonkor.Core/Services/JavaScriptParser.cs)), and Esprima cannot parse TypeScript — for most `.ts/.tsx` files imports are silently discarded (lines 88-99). Effectively **no** JS import edge connects real nodes today. (Known direction: the JS/TS plugin family with a Node sidecar replaces this parser.)
- **Fix:** remove `ToLowerInvariant()`; use the same canonical path form as the scanner (a shared helper).

### BUG-013 — PHP parser: `metadata.php` creates phantom `EXTENDS` edges from *every* `'k' => 'v'` pair
- **Severity:** High · **Status:** Confirmed
- **Location:** [PhpModuleParser.cs:29](../src/Shonkor.Core/Services/PhpModuleParser.cs) (`['"](\w+)['"]\s*=>\s*['"]([^'"]+)['"]` applied to the **whole file**, line 148)
- **Impact:** A normal OXID `metadata.php` (`'id'`, `'title'`, `'author'`, `'templates'`, `'settings'`, …) creates dozens of bogus edges like `My Module EXTENDS title` — module dependency and impact queries are flooded with garbage.
- **Related (Medium):** `^\s*class` misses `abstract class`/`final class` and namespaced base classes (`\w+` stops at the `\`) — precisely the base-class layer of the OXID module chains is missing ([PhpModuleParser.cs:21](../src/Shonkor.Core/Services/PhpModuleParser.cs)).
- **Fix:** first isolate the `'extend' => [ … ]` block (brace balancing), apply the pair pattern only within it; class regex: `^\s*(?:final\s+|abstract\s+)*class\s+(\w+)\s+extends\s+([\w\\]+)`.

### BUG-014 — C# type-ID collisions: same-named types in one file are merged into a single node
- **Severity:** High · **Status:** Confirmed
- **Location:** [CsharpNodeId.cs:33](../src/Shonkor.Core/Services/CsharpNodeId.cs) (`ForType = {filePath}::{typeName}` — without namespace, generic arity, nesting chain); member IDs collide transitively ([RoslynAstParser.cs:152](../src/Shonkor.Core/Services/RoslynAstParser.cs) uses only the innermost type name)
- **Trigger:** `namespace A { class C {} } namespace B { class C {} }` in one file; `class Foo {}` + `class Foo<T> {}`; two classes with a same-named nested `class Builder`.
- **Impact:** Two entities merge into one node (last upsert wins) — wrong call hierarchies, wrong impact/rename results, content/lines of one type overwrite the other. The `CsharpNodeId` remarks document only the partial-type ambiguity; this collision is undocumented.
- **Fix:** include namespace + nesting chain + generic arity in the ID, mirrored in `RoslynSemantics.ToNodeId`; **bump `SchemeVersion`**.

### BUG-015 — Drift reconcile never converges (two causes): excluded files are revived; new binary/oversized files loop
- **Severity:** High · **Status:** Confirmed
- **Location:** (a) [GraphIndexScanner.cs:395-408](../src/Shonkor.Infrastructure/Services/GraphIndexScanner.cs): `ReconcileDriftAsync` sends `drift.Deleted` through `ScanFileAsync`, which does not know exclude patterns — an existing, parseable, but **excluded** file is re-indexed instead of removed → the next drift report reports it as `Deleted` again, the background reconciler re-indexes it **every cycle**; explicitly excluded content stays in the graph permanently. (b) [GraphIndexScanner.cs:345-361](../src/Shonkor.Infrastructure/Services/GraphIndexScanner.cs): the size/binary check applies only to the `changed` branch — a new >5-MB or binary file with a parseable extension lands in `New`, is discarded by `ScanFileAsync`, and reappears as `New` on the next pass. `DriftReport.IsClean` never becomes `true` in either case.
- **Fix:** (a) handle `drift.Deleted` directly via `DeleteByFilePathAsync` + `MaintainReferencersAsync`, or pass exclude patterns through into `ScanFileAsync`; (b) apply the size/binary filter to the `added` branch as well.

---

## Medium

### BUG-016 — The FTS-LIKE fallback delivers an *unordered* list that RRF interprets as a ranking
- **Status:** Confirmed · **Location:** [SqliteGraphStorageProvider.cs:288-318](../src/Shonkor.Infrastructure/Storage/SqliteGraphStorageProvider.cs); consumers `search_hybrid` ([FindTools.cs:248/267](../src/Shonkor.Infrastructure/Services/Mcp/Tools/FindTools.cs)), web search
- If FTS5 throws a syntax error (colons, slashes, `<`/`>` — i.e. exactly identifier queries like `Foo::Bar`, `IFoo<T>`), a LIKE fallback kicks in **without `ORDER BY`** with score 1.0. RRF weights the list position as a rank → the keyword half of the fusion is table randomness; irrelevant nodes can displace the real hit. **Fix:** a deterministic relevance proxy (`ORDER BY CASE WHEN Name LIKE @q THEN 0 ELSE 1 END, Name`) or quote the FTS query instead of falling back.

### BUG-017 — No similarity floor, scores hidden from the client (hallucination path)
- **Status:** Confirmed · **Location:** `SearchSemanticAsync` (no threshold); [FindTools.cs:197-202, 273-278](../src/Shonkor.Infrastructure/Services/Mcp/Tools/FindTools.cs) (output line without score)
- A query for a concept not present in the corpus nonetheless returns `limit` authoritative-looking hits. **Fix:** a configurable min-cosine floor (~0.3–0.4 for nomic) and/or the score in the output line. Additionally in the RAG path: on `contextNodes.Count == 0` don't even generate, but return abstention (today the path relies solely on the prompt compliance of the small model).

### BUG-018 — CLI embed pass reads a different config source than the query side → silent total failure of semantic search
- **Status:** Confirmed · **Location:** [Program.cs:323-325](../src/Shonkor.CLI/Program.cs) (`AddEnvironmentVariables()` **only**) vs. Web/appsettings on the query side
- If `EmbeddingService:OllamaModel`/`:DocumentPrefix` is set in appsettings, the CLI pass indexes under a different model/prefix than the query. A different model → the dimension guard skips **every** node → semantics empty, hybrid silently degrades to FTS-only, with no diagnostic. **Fix:** the CLI loads the same appsettings file.

### BUG-019 — Enrichment starvation: permanently failing nodes block the queue forever
- **Status:** Confirmed · **Location:** [SqliteGraphStorageProvider.cs:1389-1393](../src/Shonkor.Infrastructure/Storage/SqliteGraphStorageProvider.cs) (`LIMIT @BatchSize`, no `ORDER BY`, no retry counter); [SemanticEnrichmentService.cs:267-272](../src/Shonkor.Web/Services/SemanticEnrichmentService.cs) (the flag stays 1)
- If ≥ BatchSize (16) nodes deterministically fail, every cycle selects the same batch — no further node is ever embedded. **Fix:** a retry counter / last-attempt column / `ORDER BY RANDOM()`.

### BUG-020 — MCP error format: tool errors as protocol errors, parse errors without a response, notifications → HTTP 400
- **Status:** Confirmed · **Location:** [McpRequestHandler.cs:104-109,151-195](../src/Shonkor.Infrastructure/Services/McpRequestHandler.cs); [McpEndpoints.cs:82-86](../src/Shonkor.Web/Endpoints/McpEndpoints.cs)
- (a) Tool execution errors arrive as JSON-RPC `-32603` instead of a `result` with `isError: true` (MCP spec) — many hosts show this as an opaque client error instead of giving it to the model for self-correction; `SendToolResponse` cannot do `isError` at all. (b) Invalid JSON → `null` instead of `-32700` with `id: null`; a missing `method` → wrong code `-32601` instead of `-32600`. (c) The relay turns the correct `null` response to notifications into an HTTP 400 — **every standard session begins with an error** (`notifications/initialized`). **Fix:** `isError` support; correct error codes; notifications → 202/empty.

### BUG-021 — No cancellation in the tool pipeline; a serial stdio loop → one hanging tool blocks the entire server
- **Status:** Confirmed · **Location:** [IMcpTool.cs:30](../src/Shonkor.Infrastructure/Services/Mcp/IMcpTool.cs) (no `CancellationToken`); [McpRequestHandler.cs:71-97](../src/Shonkor.Infrastructure/Services/McpRequestHandler.cs) (awaits before the next read — `notifications/cancelled` cannot even be read); the relay does not pass `RequestAborted` through
- A hanging backend call (e.g. `GenerateEmbeddingAsync` in [FindTools.cs:185](../src/Shonkor.Infrastructure/Services/Mcp/Tools/FindTools.cs), without a token/timeout) blocks the stdio server until the process is killed; aborted HTTP requests run full-graph loads (`audit`) to completion. **Fix:** a token through the pipeline; read the stdio loop concurrently.

### BUG-022 — Retry loops swallow cancellation, retry non-transient errors, and discard Ollama error bodies
- **Status:** Confirmed · **Location:** [OllamaSemanticAnalyzer.cs:128-137,227-236](../src/Shonkor.Infrastructure/Services/OllamaSemanticAnalyzer.cs); [OllamaEmbeddingService.cs:87-96](../src/Shonkor.Infrastructure/Services/OllamaEmbeddingService.cs); `EnsureSuccessStatusCode` without a body log in 4 places
- `catch (Exception)` catches the caller's `OperationCanceledException` (cancellation is logged as a "retryable" error); a 404 "model not found" is retried 3× with the full 2-minute timeout (up to ~6 min/node, across hundreds of nodes in enrichment: hours); Ollama's JSON error body (`{"error":"model 'x' not found…"}`) is never read — operators see only the status code. **Fix:** `catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }` first; retry only transient errors; the error body into the message.

### BUG-023 — Node analysis: truncation mid-line/mid-surrogate-pair + missing determinism options → summary and vector churn
- **Status:** Confirmed · **Location:** [OllamaSemanticAnalyzer.cs:40-44](../src/Shonkor.Infrastructure/Services/OllamaSemanticAnalyzer.cs) (`[..8000]` instead of the existing `TruncateAtLineBoundary`, line 145) and [:63-69](../src/Shonkor.Infrastructure/Services/OllamaSemanticAnalyzer.cs) (no `temperature`/`seed`, while the RAG paths explicitly set `temperature = 0`)
- The model gets no signal that the code was truncated (describes a fragment as the whole; a split surrogate pair produces an invalid string); a re-analysis of an unchanged node returns a different summary → since the summary feeds into the embedding text (`EmbeddingTextBuilder`), every enrichment run shifts the vector space — unstable search results across re-indexes. **Fix:** use `TruncateAtLineBoundary`; `options = new { temperature = 0 }`.

### BUG-024 — The capsule "budget" does not bound the capsule
- **Status:** Confirmed · **Location:** [ContextCapsuleSynthesizer.cs:261-310](../src/Shonkor.Core/Services/ContextCapsuleSynthesizer.cs)
- Seeds never decrement `remaining`; header/signature/summary lines and the Mermaid diagram are emitted for **every** node and never counted; `MaxNodes` defaults to `null`. A 2-hop hub expansion exceeds `MaxContentChars` without bound, while the trailer/omission note falsely claims "budget reached / ~N tokens". Worst case: the "token-optimized" capsule blows the context of the consuming LLM. **Fix:** count header/diagram/seed bodies against a (second) budget; give `MaxNodes` a sensible default.

### BUG-025 — The semantic linker stamps heuristic fallback edges as `Extracted`; provenance is never corrected on re-scan
- **Status:** Confirmed · **Location:** [SemanticCsharpLinker.cs:120-176](../src/Shonkor.Infrastructure/Services/SemanticCsharpLinker.cs) (provenance not set → default `Extracted`, also for the name-based edges from `ResolveUnresolvedByNameAsync`, which create an edge to *every* same-named definition); [SqliteGraphStorageProvider.cs:183](../src/Shonkor.Infrastructure/Storage/SqliteGraphStorageProvider.cs) (`INSERT OR IGNORE` never updates provenance)
- This corrupts exactly the trust signal that `references`/`find_usages` offer via a `provenance` filter (CrossTechLinker tags the same cases correctly as `Inferred`/`Ambiguous`). **Fix:** carry provenance per edge (`defs.Count > 1 ? Ambiguous : Inferred` for the fallback); upsert to `ON CONFLICT … DO UPDATE SET Provenance = …`.

### BUG-026 — Unchunked `IN (…)` in the subgraph/incident edge fetch → "too many SQL variables" on large traversals
- **Status:** Confirmed · **Location:** [SqliteGraphStorageProvider.cs:1346-1372](../src/Shonkor.Infrastructure/Storage/SqliteGraphStorageProvider.cs) (`GetEdgesBetweenNodesAsync`), [:542-552](../src/Shonkor.Infrastructure/Storage/SqliteGraphStorageProvider.cs) (`GetIncidentEdgesAsync`) — while the same file declares `MaxSqlParameters = 900` and chunks everywhere else
- `find_path`/`get_subgraph` with a high `maxHops` on a large repo → a `SqliteException` instead of a result. **Fix:** chunk the ID set (one-sided `IN` + in-memory filter via `HashSet`).

### BUG-027 — Use-after-dispose on cached storage providers under concurrent requests
- **Status:** Confirmed (race window) · **Location:** [ProjectManager.cs:259-262, 377-384](../src/Shonkor.Infrastructure/Services/ProjectManager.cs)
- `RefreshStorageProvider`/`DeleteProject` dispose the shared provider while in-flight queries of other clients use the same reference → `ObjectDisposedException` as `-32603`. Additionally, two parallel first calls can initialize two providers against the same SQLite file (GetOrAdd race, lines 285-298). **Fix:** reference counting/drain before dispose.

### BUG-028 — `SqliteGraphStorageProvider.Dispose` does not clear the connection pool → the DB file stays locked on Windows
- **Status:** Confirmed · **Location:** [SqliteGraphStorageProvider.cs:58-64, 1231-1234](../src/Shonkor.Infrastructure/Storage/SqliteGraphStorageProvider.cs)
- After `DeleteProject`/`RefreshStorageProvider` pooled connections keep `.db`/`-wal`/`-shm` open — delete/move fails with a sharing violation. **Fix:** `SqliteConnection.ClearPool(...)` in `Dispose()`.

### BUG-029 — `SemanticCompilationCache.Invalidate` races with readers: a stale compilation escapes and is written back
- **Status:** Confirmed (window) · **Location:** [SemanticCompilationCache.cs:121-129](../src/Shonkor.Infrastructure/Services/SemanticCompilationCache.cs) (gateless `entry.Built = false; entry.Compilation = null;` without volatile/Interlocked); `ApplyEditsAsync` writes the compilation captured before the invalidate back in line 111. Also no mtime/hash validation: externally changed files (git checkout) are served stale until the next explicit invalidate (suspected regarding real-world frequency). **Fix:** remove the entry atomically from the dictionary (`TryRemove`) instead of nulling fields.

### BUG-030 — In-memory shared cache: `busy_timeout` does not cover `SQLITE_LOCKED`
- **Status:** Suspected · **Location:** [SqliteGraphStorageProvider.cs:40-55, 76-81](../src/Shonkor.Infrastructure/Storage/SqliteGraphStorageProvider.cs)
- Shared-cache connections compete over table locks that fail immediately with `SQLITE_LOCKED` (busy_timeout only retries `SQLITE_BUSY`) — a parallel write+read on the same in-memory provider can throw "database table is locked". To confirm: a concurrent test against the in-memory mode. **Fix:** a `SemaphoreSlim(1,1)` for the in-memory case.

### BUG-031 — `Console.Input/OutputEncoding` setters can kill the stdio server at startup (headless spawn)
- **Status:** Suspected (environment-dependent) · **Location:** [McpRequestHandler.cs:66-67](../src/Shonkor.Infrastructure/Services/McpRequestHandler.cs); [McpProxyClient.cs:79-80](../src/Shonkor.CLI/McpProxyClient.cs)
- On Windows the setters throw `IOException` when no console handle exists — which is exactly how MCP hosts spawn stdio servers. To confirm: start with `CREATE_NO_WINDOW`/detached. **Fix:** guard the setters; output via a UTF-8 `StreamWriter` over `OpenStandardOutput()`.

### BUG-032 — Prompt injection: retrieved content flows unencapsulated between server instruction text
- **Status:** Suspected (by construction) · **Location:** [McpToolContext.cs:189-190](../src/Shonkor.Infrastructure/Services/Mcp/McpToolContext.cs) (node summaries inline on edge lines), [McpToolHelpers.cs:105-117](../src/Shonkor.Infrastructure/Services/Mcp/McpToolHelpers.cs) (snippets), `get_open_threads` prints stored names/status raw; in addition [SurprisingConnectionExplainer.cs:20-25](../src/Shonkor.Core/Services/SurprisingConnectionExplainer.cs) interpolates node *names* into the NUTZERFRAGE section of the RAG prompt framed as trusted
- Indexed content (Markdown docs, comments, nodes written via `record` by other clients) sits on the same lines as real tool guidance ("Suggested starting points" with ready-to-run tool calls) — a prepared document can slip instructions to the consuming agent. A multi-line summary already breaks the line format today. **Fix:** put retrieved `Summary`/`Content` texts into clearly labeled fences ("untrusted indexed content"), strip newlines from summaries; names into the data section of the prompt.

### BUG-033 — UTF-16 source files are treated as "binary": silent skip + graph limbo without a staleness signal
- **Status:** Confirmed · **Location:** [GraphIndexScanner.cs:694-711](../src/Shonkor.Infrastructure/Services/GraphIndexScanner.cs) (NUL-byte scan; UTF-16 is ~50% NUL) with the skip before the hash check and `filesToClear` (lines 127-138)
- PowerShell 5.1 `Out-File` writes UTF-16 LE by default. A previously indexed file that becomes UTF-16 (or grows > 5 MB) keeps its old nodes **forever** — without a drift signal (`DetectDriftAsync` skips it too). **Fix:** BOM detection (FF FE/FE FF) before the NUL scan; on skip decide explicitly: delete nodes or mark them stale.

### BUG-034 — Record primary constructors: the semantic linker and the parser produce inconsistent ctor IDs → dangling CALLS edges
- **Status:** Confirmed (logic mismatch) · **Location:** [RoslynSemantics.cs:73-98](../src/Shonkor.Core/Services/RoslynSemantics.cs) vs. [RoslynAstParser.cs:220, 279-293](../src/Shonkor.Core/Services/RoslynAstParser.cs)
- The primary ctor of a record is a Constructor symbol without a `ConstructorDeclarationSyntax` — `ToNodeId` produces an ID for which no node exists; at equal arity the overload-span counter of both sides additionally diverges, so even the ID of the explicit ctor differs. **Fix:** handle primary ctors specially in `OverloadSpan`/`ToNodeId`, mirror the rule in the parser.

### BUG-035 — Markdown parser: headers in code fences become phantom sections; link targets keep `#fragment`/`"title"`, non-HTTP schemes become paths
- **Status:** Confirmed · **Location:** [MarkdownHierarchyParser.cs:19, 26, 67, 111-121](../src/Shonkor.Core/Services/MarkdownHierarchyParser.cs)
- Every `# Comment` line in a ```bash block becomes a `MarkdownSection` node (and shifts all subsequent section IDs via the running index); `[x](./file.md#install)` → the path ends in `#install`; `mailto:`/`//cdn…` pass the `(?!https?://|#)` guard (also case-sensitively) and are combined via `Path.GetFullPath` into nonsense → dangling REFERENCES edges. **Fix:** strip fenced blocks up front; remove fragment/title from group 2; exclude every `^[a-z][a-z0-9+.-]*:` scheme and `//`.

### BUG-036 — GraphQL parser: regex without word boundaries/comment awareness creates phantom operations; `...on` (without a space) is missed
- **Status:** Confirmed · **Location:** [GraphQLParser.cs:25, 28, 34, 141-151](../src/Shonkor.Core/Services/GraphQLParser.cs)
- `query\s+(\w+)` with `IgnoreCase` matches in `# query GetUser` comments and as a substring (`subquery Foo` → phantom query `Foo`); `\.\.\.\s+on\s+` requires whitespace → the very common `...on Promo` yields empty `referencedTemplates`; in addition every node in the file gets the **union** of all inline-fragment targets. **Fix:** `(?m)^\s*query\b`, strip comments, `\.\.\.\s*on\s+`, assign templates per operation.

### BUG-037 — ProjectManager: reload/save races and non-atomic writes to `projects.json`
- **Status:** Confirmed (window) · **Location:** [ProjectManager.cs:105-127, 455-499, 502-625](../src/Shonkor.Infrastructure/Services/ProjectManager.cs)
- `LoadProjects` parses outside the lock and then swaps `_projects/_users` wholesale — a parallel `AddProject` can transiently disappear (incl. a 401 for a freshly created user); `SaveProjects` does not update `_lastLoadTime` (every save forces a re-load and widens the window); writes are non-atomic — a crash mid-write corrupts the file, and `LoadProjects` then silently registers the workspace as the default project on an empty in-memory state (the registry effectively discarded). In addition, the prefix check in `SaveProjectConfig` (line 472) lacks the separator guard. **Fix:** temp file + `File.Replace`; stamp `_lastLoadTime`; the reload decision+swap under one lock; re-resolve `project` inside the lock.

### BUG-038 — AssemblyPluginLoader: ALC leak on partial failure; the tamper check covers only the entry assembly
- **Status:** Confirmed · **Location:** [AssemblyPluginLoader.cs:96-141, 177](../src/Shonkor.Infrastructure/Services/AssemblyPluginLoader.cs)
- If `GetTypes()`/`CreateInstance` throws after context creation, the collectible `AssemblyLoadContext` is never unloaded (a leak; already instantiated parsers stay bound). Only `EntryAssemblySha256` is verified — dependency DLLs in the plugin folder are loaded by the `AssemblyDependencyResolver` **without** a hash check, contrary to the documented guarantee. **Fix:** per-plugin try/finally with `context.Unload()`; hashes for all DLLs of the package.

### BUG-039 — `SurprisingConnections` reports structurally contained pairs as "surprising"
- **Status:** Suspected (logic clear, dominance effect needs data verification) · **Location:** [GraphAnalytics.cs:205-211](../src/Shonkor.Core/Services/GraphAnalytics.cs)
- With the default `includeStructural = false`, `CONTAINS` edges are **excluded** from the linked set — a file node and the contained class count as "not connected", but their embeddings are nearly identical → trivial parent/child pairs can flood the top N. The flag semantics are inverted for this method: an existing structural edge *is* a connection. **Fix:** always include structural edges in the linked set.

---

## Low

| ID | Finding | Status | Location |
|---|---|---|---|
| BUG-040 | RRF tie-break depends on `Dictionary` enumeration order — edge cases at position `maxResults` non-deterministic, contrary to the documented determinism guarantee. Fix: `.ThenBy(kv => kv.Key, StringComparer.Ordinal)` | Confirmed | [HybridFusion.cs:40-42](../src/Shonkor.Core/Services/HybridFusion.cs) |
| BUG-041 | A corrupt embedding blob (`Length % 4 != 0`) can throw `BlockCopy` → the whole semantic search fails instead of skipping the row; `GetNodesWithEmbeddingsAsync` handles the same case differently (truncates) | Confirmed (needs corrupt data) | [SqliteGraphStorageProvider.cs:384-388](../src/Shonkor.Infrastructure/Storage/SqliteGraphStorageProvider.cs) |
| BUG-042 | `limit=0` → internal `ArgumentOutOfRange` as `-32603`; `limit` near `int.MaxValue` → overflow (`limit * 2`, `maxResults * 4`) → negative capacity | Confirmed | [FindTools.cs:44,126,244](../src/Shonkor.Infrastructure/Services/Mcp/Tools/FindTools.cs) |
| BUG-043 | `hops`/`maxHops` unclamped in `get_subgraph`, `generate_capsule`, `find_path`, and `/api/rag/query` (there also: the CancellationToken is not passed through to storage calls) — arbitrarily expensive traversals by one client | Confirmed | [ReadTools.cs:221,315](../src/Shonkor.Infrastructure/Services/Mcp/Tools/ReadTools.cs), [AnalyzeTools.cs:473](../src/Shonkor.Infrastructure/Services/Mcp/Tools/AnalyzeTools.cs), [GraphRagEndpoints.cs:40](../src/Shonkor.Web/Endpoints/GraphRagEndpoints.cs) |
| BUG-044 | `GetContentHashesAsync` uses an `OrdinalIgnoreCase` dictionary on node IDs, the rest of the provider is `Ordinal` — a case collision corrupts the change detector | Confirmed | [SqliteGraphStorageProvider.cs:849](../src/Shonkor.Infrastructure/Storage/SqliteGraphStorageProvider.cs) |
| BUG-045 | `TryExecuteAsync` swallows **all** `SqliteException`s during migrations (intended only for "duplicate column") — lock/IO errors leave missing columns with a confusing follow-up error | Confirmed | [SqliteSchema.cs:182-186](../src/Shonkor.Infrastructure/Storage/SqliteSchema.cs) |
| BUG-046 | `NodesFts` depends on the implicit rowid of a TEXT-PK table — a future `VACUUM` silently remaps all FTS entries to the wrong nodes (counts stay the same, the drift guard blind) | Suspected (no VACUUM in the code today) | [SqliteSchema.cs:112-120](../src/Shonkor.Infrastructure/Storage/SqliteSchema.cs) |
| BUG-047 | Orphaned `HelixModule` nodes accumulate: no `FilePath` → no delete path ever applies | Confirmed | [CrossTechLinker.cs:161-204](../src/Shonkor.Infrastructure/Services/CrossTechLinker.cs) |
| BUG-048 | Path-casing drift (Windows): the hash map is `OrdinalIgnoreCase`, SQL `FilePath` comparisons are byte-sensitive → on a changing root spelling duplicates per symbol that the stale sweep does not remove | Suspected | [GraphIndexScanner.cs:145](../src/Shonkor.Infrastructure/Services/GraphIndexScanner.cs) + provider `FilePath` queries |
| BUG-049 | Mermaid escaping wrong: `\"` is not a valid Mermaid escape (correct: `#quot;`), edge labels (`\|`) not escaped at all → diagram parse errors (cf. commit b76a581, the same bug type in docs) | Confirmed | [ContextCapsuleSynthesizer.cs:112-116,143-144](../src/Shonkor.Core/Services/ContextCapsuleSynthesizer.cs) |
| BUG-050 | RAG: a 200 response without a `response` field delivers the fallback sentence "Es konnte keine Antwort generiert werden." as a normal answer (no throw/log); `SurprisingConnectionExplainer` persists this as an `[INFERRED]` explanation | Confirmed | [OllamaSemanticAnalyzer.cs:225](../src/Shonkor.Infrastructure/Services/OllamaSemanticAnalyzer.cs) |
| BUG-051 | Empty/whitespace input → a 0-dim embedding instead of an error: the semantic search silently matches nothing (indistinguishable from "no hits"); dead code `return Array.Empty<float>()` on line 99 | Confirmed | [OllamaEmbeddingService.cs:47-50,99](../src/Shonkor.Infrastructure/Services/OllamaEmbeddingService.cs) |
| BUG-052 | `Timeout` mutation on the injected HttpClient in the constructor — safe with today's typed-client registration, but the same footgun the adjacent comment explicitly avoids for `BaseAddress` | Suspected (latent) | [OllamaSemanticAnalyzer.cs:31-34](../src/Shonkor.Infrastructure/Services/OllamaSemanticAnalyzer.cs), [OllamaEmbeddingService.cs:36-39](../src/Shonkor.Infrastructure/Services/OllamaEmbeddingService.cs) |
| BUG-053 | Injection-scanner regexes without matchTimeout/NonBacktracking over attacker-controlled repo content — pathological whitespace runs can stall the indexing | Suspected | [SuspiciousContentPostProcessor.cs:27-33](../src/Shonkor.Infrastructure/Services/SuspiciousContentPostProcessor.cs) |
| BUG-054 | MCP: `initialize` echoes back any client `protocolVersion` (instead of only supported ones); `ex.Message`/absolute server paths reach remote clients (`get_diagnostics`, `-32603` texts); `-k/--key` on the command line is visible in process lists; the proxy writes every 2xx body to stdout unvalidated (the HTML of a captive-portal page corrupts the framing) | Confirmed | [McpRequestHandler.cs:133-136,161,194](../src/Shonkor.Infrastructure/Services/McpRequestHandler.cs), [MetaTools.cs:172-175](../src/Shonkor.Infrastructure/Services/Mcp/Tools/MetaTools.cs), [McpProxyClient.cs:33-37,126-133](../src/Shonkor.CLI/McpProxyClient.cs) |
| BUG-055 | Enum/method/property/ctor nodes never set `EndLine` (→ the `get_source` fallback reads a blanket 40 lines); Markdown sections carry no line numbers at all | Confirmed | [RoslynAstParser.cs:121-133,156-170,192-204,227-240](../src/Shonkor.Core/Services/RoslynAstParser.cs), [MarkdownHierarchyParser.cs:76-87](../src/Shonkor.Core/Services/MarkdownHierarchyParser.cs) |
| BUG-056 | A parser exception mid-file: `filesToClear.Add` happens **before** parsing — on a transient parser error the old (valid) nodes of the file are deleted and nothing is added back until the file changes again | Confirmed | [GraphIndexScanner.cs:151,179-182](../src/Shonkor.Infrastructure/Services/GraphIndexScanner.cs) |
| BUG-057 | The scan buffers all nodes/edges of the entire repo in memory before the first upsert (first index of very large repos: hundreds of MB+); `IndexResult` metrics count skips as "scanned" and upserts as "created" | Confirmed | [GraphIndexScanner.cs:110-122,163-165,209-216](../src/Shonkor.Infrastructure/Services/GraphIndexScanner.cs) |
| BUG-058 | Non-UTF-8 legacy encodings (ISO-8859-1, older OXID shops) are replaced with U+FFFD — names/FTS polluted, citations degraded | Suspected | [GraphIndexScanner.cs:140](../src/Shonkor.Infrastructure/Services/GraphIndexScanner.cs) |
| BUG-059 | The `IsLikelyInterface` heuristic (I + capital letter) turns base classes like `IOManager` into `IMPLEMENTS` instead of `EXTENDS`; partial-type references are pinned to an arbitrary file (`DeclaringSyntaxReferences.FirstOrDefault`) | Suspected/Confirmed | [RoslynAstParser.cs:476-477](../src/Shonkor.Core/Services/RoslynAstParser.cs), [RoslynSemantics.cs:67](../src/Shonkor.Core/Services/RoslynSemantics.cs) |

Also documented, but not counted as a new defect: partial-type overloads of equal arity produce dangling semantic edges (already described in the `CsharpNodeId` remarks as a known residual ambiguity); `AddProject` accepts arbitrary filesystem paths (a read-anything primitive, should the project API ever be exposed beyond admin — [ProjectManager.cs:173-211](../src/Shonkor.Infrastructure/Services/ProjectManager.cs)); benchmarks embed queries via the kind-less overload (document prefix) and thereby do not measure production ([RetrievalBenchmark.cs:68](../src/Shonkor.Bench/RetrievalBenchmark.cs)).

---

## Checked and clean

- **RRF formula** (`HybridFusion`): `1/(k0 + rank + 1)` with a 0-based rank ≡ standard; weights symmetric. BM25 sort direction correct (negative = better, `Math.Abs` merely cosmetic).
- **Cosine similarity**: delegated to `TensorPrimitives`; the dimension guard prevents cross-model dim mixing (apart from BUG-005/018).
- **Batch embedding assignment**: does not exist — one embedding per node, the ID in the same closure; no N↔M swap risk.
- **Embedding truncation on the index path**: both index paths (web worker, CLI) run through `EmbeddingTextBuilder` (body head+tail-bounded to 1500 characters, the header always survives) — an agent suspicion of "input is never truncated" was thereby **refuted**.
- **Graph traversals**: `GraphPathFinder` (BFS, visited set, direction per hop correct), `references`/`call_hierarchy`/`related_tests` (visited sets, caps), Brandes centrality, Louvain/BFS communities — all correct and terminating.
- **Stale-embedding invalidation on re-ingest** (a changed file → clear + `NeedsSemanticAnalysis=1`) and the **edge cascade** on `DeleteByFilePathsAsync` (both directions, one transaction).
- **SQL injection**: all user inputs parameterized; interpolated fragments are generated parameter names or a typed `int`.
- **No `.Result`/`.Wait()`/fire-and-forget** in the checked paths; DB access consistently async with `ConfigureAwait(false)`; readers/commands consistently `await using`.
- **stdio framing**: serial → no interleaved writes (the flip side: BUG-021); notification detection via `ContainsKey("id")` correct.
- **The relay's tenant lock**: `lockToContextProject` correctly ignores per-tool `projectName`; `reindex_file`/parsers are correctly withheld when tenant-locked.
- **MockSemanticAnalyzer**, **StorageBackedGraphView**, **CSharpDiagnostics** (1-based output!), **AmbiguousCSharpTypePostProcessor**: no defects.

## Not checked / blind spots

- **CMS plugins** (`Shonkor.Plugin.Kentico/Optimizely/Sitecore`) and **`Shonkor.Eval`/`Shonkor.Benchmarks`**: not read (apart from the two cited bench sites).
- **Web endpoints outside MCP/GraphRAG/Search/Stats** (Browse, Admin, Insights, Settings, Webhook, Index) and `ApiKeyMiddleware` in detail: only spot-checked.
- **Runtime behavior**: no tests/repros executed; all "Confirmed" markings are code evidence, not runtime proof. In particular BUG-001 (NaN producibility by the concrete embedding backend), BUG-030 (SQLITE_LOCKED), BUG-031 (headless spawn), and BUG-039 (dominance effect) would each be conclusively confirmable with a small test/repro.
- **Ollama behavior** (error bodies, num_ctx defaults, null-vector cases): assumed from docs/experience, not verified against a running instance.
- **Docker/k8s/deploy scripts**: out of scope.

## Recommended fix order

1. **BUG-001** (a one-liner, prevents the hang + silent mis-ranking).
2. **`ON CONFLICT(Id) DO UPDATE` instead of `INSERT OR REPLACE`** — fixes BUG-002 + BUG-005 (and the enrichment churn) in one change; together with the `/api/interactions/status` fix.
3. **BUG-003 + BUG-006** (project resolution: no silent fallback; make `set_project` over the relay honest).
4. **BUG-004** (line convention) and **BUG-014** (ID scheme, `SchemeVersion` bump) — both require a re-index, sensible to bundle.
5. **BUG-007/008/009** (the data-loss/hang class), then the Medium list.
