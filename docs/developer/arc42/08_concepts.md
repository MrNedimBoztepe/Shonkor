# arc42 Chapter 8: Concepts 💡

This chapter describes the fundamental concepts, patterns, and implementation details that form the foundation of Shonkor.

---

## 8.1 The Relational Graph Metamodel

To provide flexibility for various programming languages and configuration formats, a generalized relational metamodel is used in SQLite.

```
+---------------------------------------------------------------------------------+
|                                     NODES                                       |
+---------------------------------------------------------------------------------+
| Id (PK) | Type | Name | Content | Metadata (JSON) | FilePath | Start/End | Hash |
+---------------------------------------------------------------------------------+
                                       |
                                       | (1)
                                       |
                                       | (N)
+---------------------------------------------------------------------------------+
|                                     EDGES                                       |
+---------------------------------------------------------------------------------+
| SourceId (FK) | TargetId (FK) | RelationType                                    |
+---------------------------------------------------------------------------------+
```

### 1. Extensible Properties via Key-Value Dictionary
To keep signatures lean while remaining extensible (e.g., for Sitecore Templates or Optimizely Properties), the `GraphNode` class stores all application-specific details in a `Dictionary<string, string> Properties`. 
The SQLite repository maps known columns (like `Content` or `FilePath`) to dedicated columns – all other dynamic key-value pairs are fully automatically stored as serialized JSON in the `Metadata` column. This combines the advantages of relational data storage with schema-less flexibility.

---

## 8.2 SQLite FTS5 & Recursive CTE (The Performance Secret)

Graph traversal in relational databases is typically slowed down by joins. Shonkor uses highly optimized standard SQLite SQL to completely eliminate this problem.

### 1. FTS5 Keyword Search (Finding Seeds)
The FTS5 virtual table `NodesFts` is fully automatically synchronized with the `Nodes` table via triggers:
```sql
SELECT n.Id, n.Type, n.Name, bm25(NodesFts) AS Score
FROM NodesFts fts
JOIN Nodes n ON fts.Id = n.Id
WHERE NodesFts MATCH @query
ORDER BY Score
LIMIT @limit;
```
This delivers extremely fast, weighted entry points for any search query.

The search loads the associated edges of all matches in **a single** batch query (instead of one query per match), thus avoiding an N+1 problem with large result sets.

### 2. Recursive CTE (Graph Traversal)
A recursive Common Table Expression (CTE) join expands bidirectionally from the seed nodes over N hops:
```sql
WITH RECURSIVE Subgraph(Id, Depth) AS (
    SELECT Id, 0 FROM Nodes WHERE Id IN (@seeds)
    UNION ALL
    SELECT CASE WHEN e.SourceId = s.Id THEN e.TargetId ELSE e.SourceId END, s.Depth + 1
    FROM Edges e
    JOIN Subgraph s ON (e.SourceId = s.Id OR e.TargetId = s.Id)
    WHERE s.Depth < @hops
)
SELECT DISTINCT n.*
FROM Nodes n
JOIN (SELECT Id, MIN(Depth) AS Depth FROM Subgraph GROUP BY Id) s ON n.Id = s.Id;
```
This statement executes an N-hop traversal in a single SQL transaction. By using `UNION ALL` followed by a `MIN(Depth)` aggregation, each node is viewed from its **shortest** path – making the hop limit deterministic and independent of edge order.

---

## 8.3 Type References & Cross-Technology-Linking (Post-Scan)

Pure syntax trees provide containment (`CONTAINS`) and inheritance (`IMPLEMENTS`/`EXTENDS`), but no usage relationships. For the question "who uses type X?" to be answerable via graph traversal, a **Linker** (`CrossTechLinker`) runs after the scan:

1. The `RoslynAstParser` collects the names of referenced types per type (fields, properties, parameters, return types, `new` expressions, generics, base types) and stores them as the `referencedTypes` property.
2. The Linker resolves these names against the definition nodes (Class/Interface/Record/Struct/Enum) and creates `REFERENCES_TYPE` edges **User → Definition**.

Analogously, cross-technology edges (`BINDS_TO`, `CONTROLLER_OF`, `QUERIES_TEMPLATE`) and Helix module affiliations (`BELONGS_TO_MODULE`) are resolved. This pattern requires no additional storage API – it reads node properties and writes resolved edges back.

### Exact resolution for C# (default)
The name-matching above is a heuristic: two same-named types in different namespaces are indistinguishable, so a name match links to *every* one. By **default** (`Indexing:SemanticCSharp`, now `true`; disable with `SHONKOR_SEMANTIC_CSHARP=false` or the per-project flag), a **`SemanticCsharpLinker`** instead builds a Roslyn `CSharpCompilation` and resolves these references **exactly** via the `SemanticModel`, mapping each resolved symbol to its declaring node id (`CsharpNodeId`/`RoslynSemantics`): it disambiguates same-named types across namespaces, emits symbol-resolved `IMPLEMENTS`/`EXTENDS`/`REFERENCES_TYPE`, and additionally produces `CALLS` (method → method) edges. It is **non-lossy**: references the compilation can't resolve (partial/non-compiling checkouts) fall back to name matching (`ResolveUnresolvedByNameAsync`), so semantic mode is never worse than the syntactic resolver — only more precise. When active, the standalone name-matching for C# is skipped (the cross-tech parts still run). This is what makes the "Precision" claim true for C#; it's heavier (a compilation per scan, ~2.9× indexing on this repo's `src`), so it can be disabled per project/globally. Metadata references use the runtime ref-assembly set (no project build), which fully resolves intra-codebase symbols — references *into* un-referenced third-party types are simply skipped (they have no node anyway). A first-party `AmbiguousCSharpTypePostProcessor` additionally emits a `csharp.ambiguous-type-reference` diagnostic for referenced type names with multiple definitions, so any residual name-based over-connection stays visible via `get_diagnostics`.

---

## 8.4 Concurrency & Connection Management

Since the Web dashboard serves requests in parallel, `SqliteGraphStorageProvider` opens **a dedicated connection per operation** from the Microsoft.Data.Sqlite pool (instead of a shared, non-thread-safe connection). File DBs use WAL and a `busy_timeout`; In-Memory DBs are kept alive via a uniquely named Shared-Cache DB with a keep-alive connection. The `ProjectManager` caches providers per project via `Lazy<>` (exactly one initialization) and prevents parallel scans of the same project via `TryBeginScan`/`EndScan`.

---

## 8.5 Security Model

Shonkor is primarily local but supports a multi-tenant/SaaS mode:
* **API keys / user tokens** are stored **SHA-256 hashed** (`TokenHasher`), never in plaintext; `projects.json` holds only the hash. Presented keys are hashed and compared in constant time (`CryptographicOperations.FixedTimeEquals`). Legacy plaintext is migrated to a hash on load (self-healing), and a new user's token is returned only **once** at creation. The loopback bypass is only active in `Development` (otherwise, authentication behind a proxy collapses).
* **Plugins** are **pre-built assemblies**, not compiled source. `PluginRegistry` validates the `plugin.json` manifest + host-API version and extracts the ZIP (zip-slip guarded); `AssemblyPluginLoader` loads **only Active** plugins into a collectible `AssemblyLoadContext`. Installing a plugin **runs nothing** — per-plugin **activation** is the trust gate, and it is equivalent to executing that plugin's code. The former runtime C#-source-compilation path (the actual RCE vector) **has been removed**. `Security:EnablePlugins` is a global kill switch that **defaults to ON** (`=false` hard-disables all plugin loading); it is *not* the opt-in gate it used to be.
* **Webhooks** verify `X-Hub-Signature-256` (HMAC) and sanitize repository names against path traversal.
* Error responses disclose **no** internal details/paths; details are logged exclusively in the server log.

---

## 8.6 MCP Project Resolution & Token Efficiency

* **Project from working directory**: The MCP server derives the active project from its working directory (`FindProjectByPath`), not from the web-mutable `ActiveProjectName`. This decouples the dashboard and the AI context.
* **`set_project` is session-local, and says so (#286)**: it sets `McpToolContext.SessionProjectOverride` — per handler, i.e. per stdio process. It never calls `SetActiveProject`, which is the thing that persists `projects.json` and *would* be global. The behaviour was always right; the tool's **description** was silent about it, and an agent read "the ACTIVE project" as global and refused to switch — declining a correct action out of misplaced caution. A tool's description is what a caller reads *before* deciding, so that is where scope has to be stated, not only in the code comment.
* **An answer names the project that produced it — above all an empty one (#286)**: `ScopeSuffixAsync` appends `in project 'X' (N nodes)` to empty search/usage results. "No matches for 'X'." reads as *this symbol does not exist*; the true statement is *…does not exist in project X*, and a reader supplies the missing words themselves — wrongly, when the index points elsewhere. That is the [#157](https://github.com/MrNedimBoztepe/Shonkor/issues/157) class in our own tool surface: a plausible answer to a question nobody asked. Non-empty results are left alone: their hits already carry the project's real paths, so a scope note would be tokens for nothing.
* **Lean outputs**: `locate` and `search_graph` output compact text by default (`name -> file:line`) instead of JSON; `get_subgraph` outputs a compact `NODES`/`EDGES` block. `verbose: true` switches to full JSON. This sharply reduces the token consumption of shallow lookups (a one-line `name -> file:line` result carries a fraction of the payload of the equivalent JSON object).
* **Reusable handles**: file-path node ids are emitted as short `@/<relative>` handles, which round-trip straight back as seeds/paths — cutting token cost and avoiding brittle absolute ids.
* **Full toolset**: beyond find/read, the server exposes analysis (`references` (`used_by`/`uses`, `depth=1` flat or `depth>1` transitive blast radius / dependency tree), `call_hierarchy` (method-level callers/callees over `CALLS`, semantic mode), `find_usages`, `find_path`, `implementations_of`, `verify_exists`), the agentic **edit loop** (`get_source`, `reindex_file`, `edit_plan`, `related_tests`), and session memory (`get_open_threads`, `record`). See the [LLM Integration Manual](../../user/llm_integration.md) for the full reference.

---

## 8.9 Background Semantic Enrichment

Summaries and embeddings are produced asynchronously by a `BackgroundService` (`SemanticEnrichmentService`) that polls for nodes needing analysis and delegates to the local Ollama backend.
* **Embedding source**: the vector is computed from a structured **code document** (`type + name + signature + summary + bounded body`) by default, not just the AI summary — configurable via `Embedding:Source` (`code` | `summary`). This measurably improves natural-language ("intent") retrieval over embedding the summary alone. Each vector's **dimension and model** are stored, so a dimension change *or a same-dimension model swap* flags stale vectors for re-embedding (`MarkStaleEmbeddingsForReembedAsync`, once per process) rather than silently mixing vector spaces in search.
* **Live config**: `Embedding:Source`, `SemanticEnrichment:BatchSize` and `:MaxParallelism` are read from `IConfiguration` **per cycle** (not cached in the constructor), so dashboard edits via `/api/settings` — written to the `reloadOnChange` `appsettings.Local.json` overlay — take effect on the next cycle without a restart.
* **Bounded parallelism**: each batch is processed concurrently up to `SemanticEnrichment:MaxParallelism` (default 4); `SemanticEnrichment:BatchSize` (default 16) controls how many nodes are pulled per cycle. Client-side concurrency is safe — Ollama serializes internally when it can't parallelize, so it never costs throughput.
* **Circuit breaker**: a backend outage cancels the rest of the batch and backs off exponentially (30s → … → 15 min) instead of hammering a dead Ollama; per-node logic errors skip the node without tripping the breaker.
* **Handler lifetime**: the HTTP-backed analyzer/embedding clients are resolved from a per-cycle DI scope so `IHttpClientFactory` controls handler rotation (DNS refresh) instead of a singleton pinning one handler.

---

## 8.10 The Agentic Edit Loop

The MCP tools close a full edit loop so an AI can change code, not just read it: read precisely (`get_source` → exact body + `file:start-end`), see the impact (`references` / `find_usages` / `edit_plan`), edit, then **`reindex_file`** to refresh just that file so the graph matches the working tree. Single-file re-index (`ScanFileAsync` → `ClearFileForReindexAsync`) deletes only the file's own (outgoing/internal) edges and **preserves incoming references** other files own — re-parsing recreates the file's symbols with stable ids, so impact analysis stays intact across an edit.

**Anti-drift (cross-graph edges are a whole-graph post-pass, so a single-file edit can drift):**
* **Scoped relink (outgoing):** after re-parsing the file, `reindex_file` runs `CrossTechLinker.RelinkFileReferenceTypesAsync` to recompute **that file's outgoing `REFERENCES_TYPE` edges** (resolving only the names it references via `GetDefinitionsByNamesAsync`) — so `references` (both directions) stays correct across an edit without a full rescan.
* **Incoming-edge maintenance (reverse index):** a `TypeReferences(TypeName, NodeId, FilePath)` table — maintained during `UpsertNodesAsync`/deletes from `referencedTypes` — records who references each type by name. When a re-index renames/removes/adds a type definition, `ScanFileAsync` relinks **only the files that reference the changed name** (`GetReferencingFilePathsAsync`), removing now-dangling incoming edges and creating newly-resolvable ones — bounded to the referencers, not the repo.
* **Reconciliation (out-of-band edits):** `ReconcileDriftAsync` re-indexes only the files a `DetectDriftAsync` hash-comparison flags as changed/new/deleted; `ReconcilePathsAsync` re-indexes a known changed set (git diff / push payload) without hashing the whole tree. An opt-in `DriftReconciliationService` (`Drift:ReconcileIntervalSeconds`) runs the former periodically so edits that bypass `reindex_file` (git pull, branch switch, external editor) still converge. In **semantic mode** the reconcile then refreshes exact `CALLS`/`REFERENCES_TYPE`/`IMPLEMENTS`/`EXTENDS` edges (`SemanticCsharpLinker.RelinkFilesAsync`, scoped to the changed files + their reverse-index referencers) — not per file and not a whole rescan. A singleton **`SemanticCompilationCache`** holds the per-directory Roslyn compilation and swaps only the edited tree (`ReplaceSyntaxTree`), so repeated reconciles and a semantic-project `reindex_file` reuse it instead of rebuilding it (an O(repo) parse) each edit; a full scan invalidates it. The other cross-tech edges (`BINDS_TO`/…) still rebuild on a full scan.
* **Freshness signals:** `freshness(path)` reports `Fresh`/`Stale`/`Untracked`/`Deleted` for one file, and `freshness` (no path) reports project-wide drift (`Changed`/`New`/`Deleted`) — both by comparing on-disk SHA256 against the stored `ContentHash`. This makes drift **visible instead of silent** so the AI knows when to `reindex_file` or run a full index.

---

## 8.11 Operability: Health Probes, Logging & CI/CD

* **Health probes** (public, auth-exempt): `/health` and `/health/live` are check-less **liveness** (the process is up); `/health/ready` is **readiness** — a `StorageHealthCheck` confirms the project workspace is writable and the active graph store answers a query. Orchestrators (Docker/Kubernetes) gate traffic on readiness.
* **Structured logging**: in Production the app emits JSON console logs (`AddJsonConsole`) so a container/k8s log pipeline can parse them; Development keeps the readable console.
* **CI/CD**: GitHub Actions runs build + test on every PR to `main`; on `main` and `v*.*.*` tags it builds and pushes the hardened (non-root, `HEALTHCHECK`-equipped) Linux image to GHCR. A `.dockerignore` keeps build output and host data out of the image context.

---

## 8.7 Token Optimization & Pruning (Capsule)

For an LLM, the signal-to-noise ratio in the context is crucial.
* **Size limit**: File nodes cap stored content (the hash still covers the full content); the capsule accepts a `maxChars` budget and truncates at section boundaries.
* **Filtering**: File nodes without text content are skipped during capsule generation.
* **Formatted syntax blocks**: Node contents are wrapped in Markdown code blocks with language-specific highlight identifiers.
* **Structure over code**: An initial Mermaid.js diagram conveys relationships before the LLM analyzes the source code blocks.

---

## 8.8 GraphRAG & Semantic Search

Shonkor elevates traditional RAG (Retrieval-Augmented Generation) to **GraphRAG**. This is achieved by combining the precision of an AST-based knowledge graph with the fuzziness of semantic vector embeddings.

### 1. Vector Embeddings (Semantic Search)
In addition to the FTS5 Keyword Search, Shonkor calculates mathematical vectors (embeddings) for every code node's generated summary using a local Ollama embedding model. These embeddings are stored as BLOBs in SQLite. During a semantic search query, the system calculates the cosine similarity to find conceptually related nodes, even if exact keywords like "parser" or "ast" aren't used.

### 2. GraphRAG Context Retrieval
When the "Ask AI" feature is triggered, the system retrieves the top hits (either via Keyword or Semantic Search) and feeds them into the local LLM (`qwen2.5-coder`). Because these hits are AST nodes with clear boundaries (`StartLine`, `EndLine`, `Properties`), the LLM receives perfectly scoped, highly relevant context, eliminating hallucination and preventing context window overflow.
