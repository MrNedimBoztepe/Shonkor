# arc42 Chapter 2: Architecture Constraints ⚙️

This chapter describes the technical and organizational constraints that influence the design of Shonkor.

---

## 2.1 Technical Constraints

| Constraint | Description | Impact on Architecture |
| :--- | :--- | :--- |
| **.NET 10 (C#)** | The system must be executable on the latest .NET runtime environment. | Use of modern C# features such as file-scoped namespaces, records, pattern matching, and native AOT compatibility. |
| **SQLite (FTS5 + CTE)** | Use of SQLite as the sole database backend. | Database operations must be resolved via performant SQL commands. Recursive CTEs and FTS5 triggers must be created manually during setup. |
| **No External Servers** | No dependencies on cloud RAG systems or external SaaS databases. | All logic (parser, storage, CLI, and web host) is executed locally on the user's machine. |
| **Platform Support** | Support for Windows systems (and Linux/macOS via dotnet-core). | Use of platform-independent path separators and standardized file system access. |
| **Node ≥ v18 (JS/TS only, bring-your-own)** | Real TypeScript semantics require the TypeScript Compiler API, which only runs on Node. Node is **not bundled** and is not a host dependency — it is an *optional* runtime the `shonkor-typescript` plugin discovers on the user's machine (`NodeDiscovery.RequiredMajorVersion` = 18). | The JS/TS parser cannot live in the host: it is an out-of-process **sidecar** behind an `IFileParser` adapter in an installable plugin, with an IPC + timeout budget. Because an optional prerequisite may be absent, the plugin must carry a **degradation path** — the private `EsprimaFallbackParser` — so indexing continues (coarser) instead of failing. See [Setup Guide → Step 3](../../user/setup_guide.md#step-3-node-runtime-for-jsts-analysis). |

---

## 2.2 Organizational Constraints

* **GitFlow Model**: Consistent separation of feature development via short-lived `feature/*` branches, which are merged into a `develop` branch and ultimately into a stable `main`/`master` branch.
* **arc42 Documentation Standard**: Commitment to maintaining and continuously updating the system architecture in separate, versioned chapters.
* **Document Integrity**: Every code change requires an immediate review of the associated architecture documentation (pre-commit guideline).

---

## 2.3 Conventions

* **.NET Code Guidelines**: Adherence to official Microsoft coding guidelines (PascalCase for public members, camelCase for parameters, underscore prefix `_` for private fields).
* **SOLID, KISS, DRY**: Avoidance of code duplication through central abstractions (e.g., `IGraphStorageProvider`) and separation of parser and persistence logic.
* **Nullable Reference Types**: Mandatory activation of `<Nullable>enable</Nullable>` in all project files to prevent NullReferenceExceptions.
