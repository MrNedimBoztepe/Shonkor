# Shonkor 🧠 - High-Precision GraphRAG Sales Presentation

## Executive Pitch: The Game-Changer for Enterprise AI Coding

Artificial Intelligence is revolutionizing software development, but traditional AI assistants fail in enterprise environments due to three massive barriers: **inaccurate context (hallucinations)**, **exploding API costs**, and **data privacy risks**.

**Shonkor** completely eliminates these barriers. It is a **100% offline-capable Precision GraphRAG engine** that breaks down source code and documentation into a local knowledge graph with compiler-level accuracy. Instead of imprecise vector searches, Shonkor delivers deterministic, mathematically exact context for LLMs – in milliseconds and with minimal token consumption.

---

## 💎 The 4 Core Value Propositions

### 1. 100% Precision Instead of "Gambling" (No Hallucinations)
* **The Problem**: Classic vector databases (Vector-RAG) slice code into arbitrary text blocks. The AI sees methods without their imports, interfaces, or class affiliations. This leads to faulty code suggestions (hallucinations).
* **Our Solution**: A Roslyn-based compiler AST parser breaks the code down into real nodes (classes, methods) and edges (inheritances, calls). The AI receives the physical, mathematically exact context.
* **Result**: **0% hallucinations** due to structural errors. The code compiles on the first try.

### 2. Fewer Tokens Per Query (ROI)
* **The Problem**: To explain complex tasks to an AI, developers often have to copy entire folders or huge sections of code into the prompt. This clogs the context window and drives up API fees (e.g., GPT-4o or Claude 3.5).
* **Our Solution**: The integrated *Context Capsule Synthesizer* performs an N-hop graph traversal and prunes irrelevant noise. The LLM receives only the mathematically relevant code parts.
* **Result**: In the reproducible benchmark, **≈ 41 % fewer tokens** per query on a mid-sized graph (up to **~88 %** on larger, hub-dense codebases) — measured against dumping the *same* retrieved subgraph in full, not against the whole repo — while raising the coverage of the symbol you actually need.

### 3. Absolute Data Security (100% Enterprise-Compliant)
* **The Problem**: Many companies prohibit the use of AI editors because code must be uploaded to external vector servers in the cloud. This violates intellectual property (IP) and GDPR guidelines.
* **Our Solution**: Shonkor operates completely autonomously and locally. The entire knowledge graph is stored in a single, lightweight SQLite file (`shonkor.db`). **0 KB of data** flows to the internet.
* **Result**: Full IP control and compliance for banks, insurance companies, and regulated industries.

### 4. Lightning-Fast Time-to-Value (Sub-Second Performance)
* **The Problem**: Indexing large repositories often takes hours with vector searches and requires expensive GPU infrastructure.
* **Our Solution**: Highly optimized, recursive SQL database queries (SQLite FTS5 + CTEs) run on standard developer laptops in milliseconds.
* **Result**: Entire repositories are indexed in **under 2 seconds**.

---

## 📊 Reliable Figures, Facts & Benchmarks

The following performance metrics were collected in a real production environment and demonstrate Shonkor's superior performance:

| Metric | Value | Evidence / Technical Basis |
| :--- | :---: | :--- |
| **Indexing Performance** | **> 19 files / second** | 34 complex source code files fully indexed in **1.80 seconds**. |
| **Database Size (Footprint)** | **352 KB** | Local SQLite database (`shonkor.db`) – highly compressed and directly versionable in Git. |
| **Search Latency (Seed Finding)** | **< 5 milliseconds** | BM25-weighted SQLite FTS5 (Full-Text Search) across the entire source code. |
| **Traversal Latency** | **< 10 milliseconds** | Recursive Common Table Expressions (CTEs) resolve N-hop connections at the SQL level. |
| **Token Savings** | **≈ 41 % reduction** (up to ~88 % on hub-dense graphs) | Budget-aware capsule vs. dumping the *same* retrieved 2-hop subgraph in full — the fair baseline. Reproducible via `Shonkor.Bench`; see the README Benchmark section. |
| **Retrieval (exact name)** | **Precision@1 0,95 / Recall@10 1,00** | FTS5 keyword search, 200 self-retrieval cases. |
| **Retrieval (plain-English intent)** | **Recall@10 0,37 → 0,97** | Keyword vs. code-embedding vector search, 150 doc-comment-derived cases (symbol name stripped). |

---

## 💰 ROI Calculation (Example for a Developer Team)

Assuming a team of **10 developers** each makes **20 complex code requests** per day to a premium LLM (GPT-4o at $5.00 per 1M input tokens), **220 working days** per year. This is an illustrative model — the actual saving depends on how much context you would otherwise send, and is anchored to the benchmark's **measured reduction band of ≈ 41 % (mid-sized graph) to ~88 % (hub-dense graph)** against dumping the same retrieved subgraph in full.

* **Baseline** — dumping the relevant retrieved subgraph in full: **~10,000 tokens/prompt** → `10 × 20 × 10,000 × $0.000005 × 220 = $2,200 / year`.
* **With Shonkor** — budget-aware capsule at the measured reduction:
  * Conservative (**41 %** cut → ~5,900 tokens): **~$1,298 / year** → **~$902 saved**.
  * Hub-dense (**88 %** cut → ~1,200 tokens): **~$264 / year** → **~$1,936 saved**.

> [!TIP]
> The real win is not just cost: at a matched token budget Shonkor covers the target symbol **98 % vs 77 %** for plain chunked RAG (+21 pp), so the LLM is far less likely to answer from incomplete context.

---

## 🛠️ How it Works: Vector-RAG vs. Shonkor GraphRAG

```mermaid
graph TD
    subgraph Vector-RAG (Probabilistic)
        A[Codebase] -->|Arbitrary Split| B[Text Slices]
        B -->|Embedding| C[Vector Search]
        C -->|Imprecise Matches| D[Possible Context]
        D -->|Missing Relationships| E[LLM Hallucination]
    end

    subgraph Shonkor GraphRAG (Deterministic)
        F[Codebase] -->|Compiler AST Parsing| G[Semantic Graph]
        G -->|SQLite FTS5 Keyword Match| H[Precise Seed Node]
        H -->|Recursive SQL CTE Traversal| I[N-Hop Subgraph]
        I -->|Context Capsule Synthesizer| J[Mermaid Diagram + Relevant Code]
        J -->|Full Logical Context| K[LLM Precise Response]
    end

    style E fill:#ffcccc,stroke:#ff0000,stroke-width:2px
    style K fill:#ccffcc,stroke:#00aa00,stroke-width:2px
```

---

## 🎯 Target Audiences & Argumentation Guide

### 1. For the CTO / Head of Development
* **Keynote**: *"Increase your developers' productivity without them wasting time searching for file paths."*
* **Main Arguments**: Compiler-accurate context ensures that AI-generated code compiles immediately. Developers don't have to painstakingly correct AI suggestions first.

### 2. For the Chief Information Security Officer (CISO)
* **Keynote**: *"Bring generative AI securely into your development without code leaking abroad."*
* **Main Arguments**: 100% offline architecture. Runs locally in a container or on laptops. No external SaaS databases required.

### 3. For the CFO / Procurement
* **Keynote**: *"Drastically reduce your monthly LLM API costs."*
* **Main Arguments**: In the reproducible benchmark, the context capsule cuts the tokens sent per query by **≈ 41 %** on a mid-sized graph — measured against dumping the same retrieved subgraph in full — and by up to **~88 %** on larger, hub-dense codebases. Fewer tokens per query means proportionally lower LLM API costs, and running fully local removes per-token SaaS fees for indexing/embeddings entirely.
