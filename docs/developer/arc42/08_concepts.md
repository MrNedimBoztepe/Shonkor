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

---

## 8.4 Concurrency & Connection Management

Since the Web dashboard serves requests in parallel, `SqliteGraphStorageProvider` opens **a dedicated connection per operation** from the Microsoft.Data.Sqlite pool (instead of a shared, non-thread-safe connection). File DBs use WAL and a `busy_timeout`; In-Memory DBs are kept alive via a uniquely named Shared-Cache DB with a keep-alive connection. The `ProjectManager` caches providers per project via `Lazy<>` (exactly one initialization) and prevents parallel scans of the same project via `TryBeginScan`/`EndScan`.

---

## 8.5 Security Model

Shonkor is primarily local but supports a multi-tenant/SaaS mode:
* **API keys / user tokens** are stored **SHA-256 hashed** (`TokenHasher`), never in plaintext; `projects.json` holds only the hash. Presented keys are hashed and compared in constant time (`CryptographicOperations.FixedTimeEquals`). Legacy plaintext is migrated to a hash on load (self-healing), and a new user's token is returned only **once** at creation. The loopback bypass is only active in `Development` (otherwise, authentication behind a proxy collapses).
* **Plugins** are an RCE vector: runtime compilation is opt-in (`Security:EnablePlugins`) and loads into an unloadable `AssemblyLoadContext`.
* **Webhooks** verify `X-Hub-Signature-256` (HMAC) and sanitize repository names against path traversal.
* Error responses disclose **no** internal details/paths; details are logged exclusively in the server log.

---

## 8.6 MCP Project Resolution & Token Efficiency

* **Project from working directory**: The MCP server derives the active project from its working directory (`FindProjectByPath`), not from the web-mutable `ActiveProjectName`. This decouples the dashboard and the AI context.
* **Lean outputs**: `locate` and `search_graph` output compact text by default (`name -> file:line`) instead of JSON; `get_subgraph` outputs a compact `NODES`/`EDGES` block. `verbose: true` switches to full JSON. This reduces the token consumption of shallow lookups by ~90%.
* **Reusable handles**: file-path node ids are emitted as short `@/<relative>` handles, which round-trip straight back as seeds/paths — cutting token cost and avoiding brittle absolute ids.
* **Full toolset**: beyond find/read, the server exposes analysis (`impact_of`, `depends_on`, `find_usages`, `find_path`, `implementations_of`, `verify_exists`), the agentic **edit loop** (`get_source`, `reindex_file`, `edit_plan`, `related_tests`), and session memory (`get_open_threads`, `record_*`). See the [LLM Integration Manual](../../user/llm_integration.md) for the full reference.

---

## 8.9 Background Semantic Enrichment

Summaries and embeddings are produced asynchronously by a `BackgroundService` (`SemanticEnrichmentService`) that polls for nodes needing analysis and delegates to the local Ollama backend.
* **Bounded parallelism**: each batch is processed concurrently up to `SemanticEnrichment:MaxParallelism` (default 4); `SemanticEnrichment:BatchSize` (default 16) controls how many nodes are pulled per cycle. Client-side concurrency is safe — Ollama serializes internally when it can't parallelize, so it never costs throughput.
* **Circuit breaker**: a backend outage cancels the rest of the batch and backs off exponentially (30s → … → 15 min) instead of hammering a dead Ollama; per-node logic errors skip the node without tripping the breaker.
* **Handler lifetime**: the HTTP-backed analyzer/embedding clients are resolved from a per-cycle DI scope so `IHttpClientFactory` controls handler rotation (DNS refresh) instead of a singleton pinning one handler.

---

## 8.10 The Agentic Edit Loop

The MCP tools close a full edit loop so an AI can change code, not just read it: read precisely (`get_source` → exact body + `file:start-end`), see the impact (`impact_of` / `find_usages` / `edit_plan`), edit, then **`reindex_file`** to refresh just that file so the graph matches the working tree. Single-file re-index (`ScanFileAsync` → `ClearFileForReindexAsync`) deletes only the file's own (outgoing/internal) edges and **preserves incoming references** other files own — re-parsing recreates the file's symbols with stable ids, so impact analysis stays intact across an edit (cross-tech links are otherwise only rebuilt on a full scan).

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
