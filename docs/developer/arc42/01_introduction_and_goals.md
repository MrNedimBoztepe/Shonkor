# arc42 Chapter 1: Introduction & Goals 🎯

This chapter describes the essential requirements and quality goals for Shonkor.

---

## 1.1 Task Description

Today's RAG systems (Retrieval-Augmented Generation) mostly rely on probabilistic vector databases and simple character slices. In practice, this leads to serious problems with codebases:
1. **Hallucinations**: The LLM sees code segments without their context (imports, class structures, inherited interfaces).
2. **Inaccuracy**: Relevant relationships (e.g., which method calls another) are often overlooked by the similarity search.
3. **High Token Consumption**: Irrelevant code blocks are loaded, which increases costs and clutters context windows.

**Shonkor** solves these problems with a **deterministic Knowledge Graph (GraphRAG)**:
* Source code and documentation are precisely decomposed into nodes (classes, methods, files, sections) and edges (dependencies, calls, inheritance).
* Search queries use a hybrid model of FTS5 full-text search for seed discovery and recursive SQL graph traversals (CTEs) for context determination.
* The result is passed to the LLM in a token-optimized capsule (including a visual representation as Mermaid.js).

---

## 1.2 Quality Goals

| Priority | Quality Goal | Description | Measurable Target Metric |
| :---: | :--- | :--- | :--- |
| **1** | **100% Precision** | No assumptions. The LLM receives exactly the physical declarations and relationships that exist in the graph. | 0% false reports about non-existent methods/classes. |
| **2** | **Portability & Autonomy** | The system runs 100% offline, without external API dependencies, and uses a local SQLite backend. | 0 KB network traffic; database size < 1 MB for standard repositories. |
| **3** | **Performance** | Indexing of local repositories in seconds. Queries and CTE traversal in milliseconds. | Indexing: > 15 files/sec. <br>Search & Traversal: < 10ms. |
| **4** | **Token Efficiency** | Pruning (truncating the graph after N hops) minimizes noise and saves expensive LLM tokens. | > 85% token savings compared to providing the entire codebase. |

---

## 1.3 Stakeholders

* **Developers / End Users**: Want precise answers from their AI assistants for complex code refactoring.
* **Enterprises / Security Officers**: Want to ensure that sensitive code structures are not transferred to external RAG servers (complete offline security).
* **System Architects**: Want to visually analyze the structural dependencies of their systems in the dashboard.

---

## 1.4 Real Project Results & Benchmarks (As of: May 2026)

The following measurement data was collected directly in the production environment during the analysis of a real code and documentation base and proves the achievement of our ambitious quality goals:

* **Indexing Performance**:
  * **Scan Speed**: **34 source code files** (.NET C#, JS, Markdown) were completely read, lexically parsed, and transferred to the graph in **just 1.80 seconds** (~19 files/second).
  * **Graph Density**: From the 34 files, **241 semantic nodes** (classes, methods, interfaces, Markdown sections) and **229 precise logical edges** (dependencies, parent-child relationships) were extracted.
* **Query Speed (FTS5 & CTE Traversal)**:
  * **Semantic Search**: BM25-weighted keyword searches across the entire source code of the database take **under 5 milliseconds**.
  * **N-Hop Graph Traversal**: Extracting a 2-hop subgraph including the physical code contents takes **under 10 milliseconds**.
* **Token Savings (Pruning & Capsule Synthesis)**:
  * For a query regarding the core class `SqliteGraphStorageProvider`, Shonkor generates a complete, prompt-ready context capsule (including Mermaid diagram and code of the relevant methods) with a size of **only 4,592 characters (~1,148 tokens)**.
  * Compared to transferring the entire codebase, this corresponds to a **token reduction of approx. 92%**. API costs for LLMs thus drop by the same factor, while response quality is significantly higher.
* **Resource Efficiency**:
  * The entire indexed SQLite database (`shonkor.db`) is only **352 KB** in size and can easily be placed under version control in the Git repository.
