# Shonkor 🧠 - arc42 Architecture Documentation

This directory contains the complete architecture documentation of the Shonkor project, structured according to the internationally proven **arc42** standard.

The documentation is intended for software architects, developers, and auditors who want to understand or extend the internal design, logical building blocks, runtime aspects, and underlying concepts of the system.

---

## 📚 Chapter Directory

### 📄 [Chapter 1: Introduction & Goals](file:///c:/Projects/Brain/docs/developer/arc42/01_introduction_and_goals.md)
* Task definition and core requirements (Precise GraphRAG, 100% offline portability).
* Quality goals (Precision over probability, performance, token pruning).
* Stakeholder overview.

### 📄 [Chapter 2: Architecture Constraints](file:///c:/Projects/Brain/docs/developer/arc42/02_architecture_constraints.md)
* Technical constraints (.NET 10, SQLite, 0 external dependencies).
* Organizational constraints (GitFlow, arc42 standard).
* Conventions (.NET Code Guidelines, Clean Code).

### 📄 [Chapter 3: System Scope and Context](file:///c:/Projects/Brain/docs/developer/arc42/03_system_scope_and_context.md)
* Business context (Data flows between developer, workspace source code, and LLMs).
* Technical context (CLI, Web dashboard, file system crawler).

### 📄 [Chapter 4: Solution Strategy](file:///c:/Projects/Brain/docs/developer/arc42/04_solution_strategy.md)
* Fundamental decisions and solution approaches.
* Why SQLite FTS5 + Recursive CTEs?
* Multi-language AST parsing concept.

### 📄 [Chapter 5: Building Block View](file:///c:/Projects/Brain/docs/developer/arc42/05_building_block_view.md)
* Static structure of the system (Level 1 and Level 2).
* Breakdown into Core, Infrastructure, CLI, and Web.
* Interface design.

### 📄 [Chapter 6: Runtime View](file:///c:/Projects/Brain/docs/developer/arc42/06_runtime_view.md)
* Dynamic behavior of the system.
* Sequence diagram for incremental indexing.
* Sequence diagram for recursive subgraph extraction and capsule synthesis.

### 📄 [Chapter 8: Concepts](file:///c:/Projects/Brain/docs/developer/arc42/08_concepts.md)
* AST graph metamodel.
* FTS5 + Recursive CTE (UNION ALL + MIN-Depth) and batch edge loading.
* Type references (`REFERENCES_TYPE`) & cross-technology linking.
* Concurrency (connection-per-operation), security model.
* MCP project resolution & token efficiency, capsule pruning.

---

## ⚖️ Quality Standards

Adherence to this documentation is continuously ensured through pre-commit checks. Any structural code change strictly requires an audit and an update to the corresponding arc42 chapter.
