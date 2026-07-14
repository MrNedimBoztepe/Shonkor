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
| **4** | **Token Efficiency** | Budget-aware capsules (seed-first, hub-capped) minimize noise and save expensive LLM tokens. | ≈ 41 % fewer tokens (up to ~88 % on hub-dense graphs) vs. dumping the *same* retrieved subgraph in full — measured, reproducible via `Shonkor.Bench`. |

---

## 1.3 Stakeholders

* **Developers / End Users**: Want precise answers from their AI assistants for complex code refactoring.
* **Enterprises / Security Officers**: Want to ensure that sensitive code structures are not transferred to external RAG servers (complete offline security).
* **System Architects**: Want to visually analyze the structural dependencies of their systems in the dashboard.

---

## 1.4 Measured Results (2026-07-14)

All figures below are **measured on a stated corpus with the stated command**, on Shonkor's own repository. Every earlier number in this section was of unknown provenance and turned out to be stale — some by a wide margin (see the note at the end) — so the rule now is: *a number here either names the corpus and the command that produces it, or it does not belong here.*

**Corpus**: this repository, `shonkor index .` (semantic C# resolution **on**, the default) → **231 files**, **2 071 nodes**, **5 152 edges**.

* **Indexing throughput**: **≈ 31 files/second** (231 files, cold full index, **7,55 s**). Semantic C# resolution builds a Roslyn compilation per scan; with `SHONKOR_SEMANTIC_CSHARP=false` the scan is materially faster but the edges are name-based, not exact.
* **Query latency** (`dotnet run --project src/Shonkor.Bench -- shonkor.db --set bench/golden/agent-queries.json --search-latency`):
  * **FTS5/BM25 seed search**: median **0,74 ms**, **p95 15 ms**.
  * **2-hop subgraph traversal** (recursive CTE): median **2,4 ms**, **p95 10,8 ms**.
  > The tail is reported on purpose. A median-only "under 5 ms" claim hides the p95 an agent actually waits on.
* **Token reduction**: **75,9 %** (481 539 → 115 978 tokens over 7 queries) — the budget-aware capsule versus **dumping the same retrieved subgraph in full** (the fair baseline; not the whole repo, which nobody would send).
* **Retrieval** (`nomic-embed-text`, 768-dim): exact-name **P@1 0,890 / Recall@10 0,991** (FTS5) and **0,945 / 0,998** (hybrid); plain-English intent **0,091 / 0,182** (FTS5) → **0,485 / 0,788** (hybrid).
* **Footprint**: the SQLite database is **20,1 MB** at this graph size *with embeddings stored*. It is **not** a "commit it to Git" artifact — `shonkor.db` is gitignored, and should be.

Reproduce everything: see the **README → The numbers** section, which pins the same values against checked-in harness output (`bench/metrics-*.json`) and is guarded by a test so it cannot silently drift.

> **Why this section was rewritten.** It previously claimed *34 files in 1.80 s (~19 files/s)*, *search under 5 ms*, *traversal under 10 ms*, a *≈ 41 %* token reduction and a **352 KB** database "which can easily be placed under version control". Measured on the current system: throughput is ~31 files/s, p95 search latency is 15 ms (not "< 5 ms"), token reduction is 75,9 %, and the database is **20,1 MB — 57× larger** than the published figure, i.e. precisely *not* something to commit. The retrieval figures quoted here previously (*Recall@10 0,37 → 0,97*) came from the **circular** `doc-intent` golden set, whose queries are the target's own doc-comment — a set the project has since explicitly disowned.
