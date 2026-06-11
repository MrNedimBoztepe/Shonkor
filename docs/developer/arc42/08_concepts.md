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
* **API Keys** are compared in constant time; the loopback bypass is only active in `Development` (otherwise, authentication behind a proxy collapses).
* **Plugins** are an RCE vector: runtime compilation is opt-in (`Security:EnablePlugins`) and loads into an unloadable `AssemblyLoadContext`.
* **Webhooks** verify `X-Hub-Signature-256` (HMAC) and sanitize repository names against path traversal.
* Error responses disclose **no** internal details/paths; details are logged exclusively in the server log.

---

## 8.6 MCP Project Resolution & Token Efficiency

* **Project from working directory**: The MCP server derives the active project from its working directory (`FindProjectByPath`), not from the web-mutable `ActiveProjectName`. This decouples the dashboard and the AI context.
* **Lean outputs**: `locate` and `search_graph` output compact text by default (`name -> file:line`) instead of JSON; `get_subgraph` outputs a compact `NODES`/`EDGES` block. `verbose: true` switches to full JSON. This reduces the token consumption of shallow lookups by ~90%.

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
