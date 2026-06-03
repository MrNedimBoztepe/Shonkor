# Shonkor 🧠 - User Manuals & End-User Guides

Welcome to the Shonkor user documentation. These guides are written for software developers who want to use Shonkor in their daily workflow to provide precise context to AI models.

---

## 📚 Table of Contents

### 1. ⚙️ [Setup & Onboarding Guide](file:///c:/Projects/Brain/docs/user/setup_guide.md)
* Step-by-step installation and startup.
* Configuration via `shonkor.json`.
* Exclusion patterns (glob patterns) for performance optimization.

### 2. 💻 [CLI Reference Manual](file:///c:/Projects/Brain/docs/user/cli_reference.md)
* Description of all CLI commands: `init`, `index`, `search`, `capsule`, and `mcp` (+`mcp install`).
* Arguments, parameters, and practical terminal examples.
* Interpretation of database statistics.

### 3. 🤖 [LLM & IDE Integration](file:///c:/Projects/Brain/docs/user/llm_integration.md)
* Live connection via the **MCP Server** (**Claude**, **Antigravity**) including tool overview.
* Usage of generated context capsules in **Cursor** and **VS Code**.
* Integration into web interfaces (**ChatGPT**, **Claude.ai**).

### 4. 📈 [Sales Presentation / Pitch Deck](file:///c:/Projects/Brain/docs/user/sales_presentation.md)
* Core value propositions, pitch, and target audiences.
* Reliable metrics, performance benchmarks, and technical proof.
* ROI calculation and token cost savings for enterprise customers.

---

## 🛠️ Quick Start Guide

1. **Create Configuration**: Run `shonkor init` to generate `shonkor.json`.
2. **Index Repository**: Run `shonkor index .` to build the SQLite graph database.
3. **Extract Context**: Run `shonkor capsule "MyModule" -h 2 -o capsule.md`.
4. **Feed AI**: Copy the generated `capsule.md` or load it directly into your prompt.
