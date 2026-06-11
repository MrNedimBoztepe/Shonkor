# arc42 Chapter 6: Runtime View 🎬

This chapter describes the dynamic behavior of the system based on essential scenarios.

---

## 6.1 Scenario 1: Incremental Indexing

This scenario illustrates the workflow when the developer invokes the command `shonkor index .` to ingest changes in their codebase into the graph.

```mermaid
sequenceDiagram
    autonumber
    actor Dev as Developer
    participant CLI as Shonkor.CLI
    participant Scan as GraphIndexScanner
    participant Db as SqliteGraphStorageProvider
    participant Parser as RoslynAstParser (C#)

    Dev->>CLI: index .
    CLI->>Db: InitializeAsync()
    Db-->>CLI: DB schema ready
    CLI->>Scan: ScanDirectoryAsync(path, exclusions)
    
    loop For each file in workspace
        Scan->>Scan: Check exclude patterns
        Scan->>Scan: Determine appropriate parser based on extension (.cs)
        Scan->>Scan: Calculate SHA256 hash of content
        
        Note over Scan, Db: (Optimization) Hash check to avoid unnecessary work
        
        Scan->>Db: DeleteByFilePathAsync(filePath)
        Db-->>Scan: Existing nodes/edges deleted
        
        Scan->>Parser: ParseAsync(filePath, content)
        Parser-->>Scan: Returns (Nodes, Edges)
        
        Scan->>Db: UpsertNodesAsync(Nodes + Hash)
        Scan->>Db: UpsertEdgesAsync(Edges)
    end
    
    Scan-->>CLI: IndexResult (Scanned, Created)
    CLI-->>Dev: Console report with statistics
```

---

## 6.2 Scenario 2: Context Synthesis (Capsule)

This scenario illustrates the workflow when the developer generates a context capsule to pass to an LLM.

```mermaid
sequenceDiagram
    autonumber
    actor Dev as Developer
    participant CLI as Shonkor.CLI
    participant Db as SqliteGraphStorageProvider
    participant Synth as ContextCapsuleSynthesizer

    Dev->>CLI: capsule "Parser" --hops 2
    CLI->>Db: SearchAsync("Parser", maxResults: 5)
    Note over Db: FTS5 MATCH with BM25 Scoring
    Db-->>CLI: Returns matching seed nodes (e.g., RoslynAstParser)
    
    CLI->>Db: GetSubgraphAsync(seeds, hops: 2)
    Note over Db: Recursive CTE join over Edges table
    Db-->>CLI: Returns all nodes & edges within 2 hops
    
    CLI->>Synth: Synthesize(Nodes, Edges)
    Note over Synth: Generates Markdown + Mermaid.js architecture graph
    Synth-->>CLI: Markdown string (Capsule)
    
    CLI->>CLI: Write capsule.md to file
    CLI-->>Dev: Success message with token statistics
```
