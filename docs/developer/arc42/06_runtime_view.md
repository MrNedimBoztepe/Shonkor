# arc42 Kapitel 6: Laufzeitsicht 🎬

Dieses Kapitel beschreibt das dynamische Verhalten des Systems anhand wesentlicher Szenarien.

---

## 6.1 Szenario 1: Inkrementelle Indexierung

Dieses Szenario zeigt den Ablauf, wenn der Entwickler den Befehl `llmbrain index .` aufruft, um Änderungen in seiner Codebasis in den Graphen einzupflegen.

```mermaid
sequenceDiagram
    autonumber
    actor Dev as Entwickler
    participant CLI as LLMBrain.CLI
    participant Scan as GraphIndexScanner
    participant Db as SqliteGraphStorageProvider
    participant Parser as RoslynAstParser (C#)

    Dev->>CLI: index .
    CLI->>Db: InitializeAsync()
    Db-->>CLI: DB schema ready
    CLI->>Scan: ScanDirectoryAsync(path, exclusions)
    
    loop Für jede Datei im Workspace
        Scan->>Scan: Überprüfe Exclude-Patterns
        Scan->>Scan: Bestimme passenden Parser anhand Extension (.cs)
        Scan->>Scan: Berechne SHA256-Hash des Inhalts
        
        Note over Scan, Db: (Optimierung) Hash-Prüfung zur Vermeidung unnötiger Arbeit
        
        Scan->>Db: DeleteByFilePathAsync(filePath)
        Db-->>Scan: Vorhandene Knoten/Kanten gelöscht
        
        Scan->>Parser: ParseAsync(filePath, content)
        Parser-->>Scan: Gibt (Nodes, Edges) zurück
        
        Scan->>Db: UpsertNodesAsync(Nodes + Hash)
        Scan->>Db: UpsertEdgesAsync(Edges)
    end
    
    Scan-->>CLI: IndexResult (Scanned, Created)
    CLI-->>Dev: Konsolen-Bericht mit Statistiken
```

---

## 6.2 Szenario 2: Kontext-Synthese (Capsule)

Dieses Szenario zeigt den Ablauf, wenn der Entwickler eine Kontextkapsel generiert, um sie an ein LLM zu übergeben.

```mermaid
sequenceDiagram
    autonumber
    actor Dev as Entwickler
    participant CLI as LLMBrain.CLI
    participant Db as SqliteGraphStorageProvider
    participant Synth as ContextCapsuleSynthesizer

    Dev->>CLI: capsule "Parser" --hops 2
    CLI->>Db: SearchAsync("Parser", maxResults: 5)
    Note over Db: FTS5 MATCH mit BM25 Scoring
    Db-->>CLI: Gibt passende Seed-Knoten zurück (z.B. RoslynAstParser)
    
    CLI->>Db: GetSubgraphAsync(seeds, hops: 2)
    Note over Db: Rekursiver CTE-Join über Edges-Tabelle
    Db-->>CLI: Gibt alle Knoten & Kanten innerhalb von 2 Hops zurück
    
    CLI->>Synth: Synthesize(Nodes, Edges)
    Note over Synth: Generiert Markdown + Mermaid.js Architektur-Graph
    Synth-->>CLI: Markdown-String (Capsule)
    
    CLI->>CLI: Schreibe capsule.md in Datei
    CLI-->>Dev: Erfolgsmeldung mit Token-Statistiken
```
