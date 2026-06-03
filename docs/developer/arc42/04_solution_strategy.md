# arc42 Chapter 4: Solution Strategy 💡

This chapter describes the fundamental architecture decisions and technological approaches of the Shonkor system.

---

## 4.1 Core Concept: Deterministic Knowledge Graph

Probabilistic RAG (e.g., vector databases) suffers from the loss of logical relationships. To provide 100% precise code context, Shonkor implements an exact directed knowledge graph.

1. **Abstraction of Code as a Graph**:
   * **Nodes** represent entities: files, classes, methods, configurations, and Markdown sections. Each node stores its type, name, and source code (`Content`).
   * **Edges** represent relationships, including `CONTAINS` (file/type contains member), `IMPLEMENTS`/`EXTENDS` (inheritance), `REFERENCES_TYPE` (type uses another type – the basis of impact analysis), `IMPORTS` (module dependency), as well as cross-technology edges (`BINDS_TO`, `CONTROLLER_OF`, `QUERIES_TEMPLATE`, `BELONGS_TO_MODULE`).
2. **Technology Choice: SQLite**:
   * Instead of complex graph databases (Neo4j, Memgraph) that require heavy server installations, Shonkor relies on **SQLite**.
   * This enables a zero-dependency, portable file (`shonkor.db`) that can be checked directly into the Git repository.
   * **FTS5 (Full-Text Search)**: Enables extremely fast, BM25-weighted keyword search across code content to find the entry points ("seeds") for search queries.
   * **Recursive Common Table Expressions (CTEs)**: Enables traversing the graph across an arbitrary number of hops ("N-hops") directly at the SQL level. This solves the typical performance problem of relational graphs in milliseconds.

---

## 4.2 Multi-Language Parser Strategy

The system breaks down different languages using specialized parser classes, all of which implement the common `IFileParser` interface:

* **Roslyn AST Parser (C#)**: Uses the official Microsoft C# compiler to break source code down into abstract syntax trees (AST). This enables the exact extraction of class declarations, inheritance, and method signatures. Detects Optimizely `[ContentType]` attributes.
* **JavaScript/TypeScript Parser**: Uses the highly performant **Esprima** library to syntactically analyze JS/TS files. Extracts import/export relationships to map module dependencies.
* **Smarty & PHP Parser**: Regex-based parser for extracting extensions (`extends`) in OXID eShop modules as well as Smarty template blocks (`[{block name="..."}]`).
* **SCS-YAML Parser (Sitecore)**: Deserializes SCS-YAML files using **YamlDotNet** to map Sitecore templates and layouts in the graph.
* **Markdown Hierarchy Parser**: Analyzes Markdown structures based on headings (`#`, `##`, `###`) and extracts relative file links to semantically link documents as accompanying material with the code.
