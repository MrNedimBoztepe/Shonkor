# Changelog

All notable changes to Shonkor are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/), and the project uses semantic versioning.

## [Unreleased]

### Changed — Plugins are now installable assemblies (runtime C# compilation removed)
- A plugin is a **pre-built assembly installed from a ZIP** and is **inert until explicitly activated**.
  `PluginRegistry` validates the `plugin.json` manifest + host-API version, extracts the package
  (zip-slip guarded) into `plugins/{id}/`, and tracks the `Installed → Active → Disabled/Failed`
  lifecycle; `AssemblyPluginLoader` loads only Active plugins into a collectible `AssemblyLoadContext`
  (the host shares the `Shonkor.Core` contract for type identity). Installing a plugin runs nothing.
- **Removed the Roslyn source-compilation plugin path** (`PluginLoader`, `StandardPluginsInstaller`,
  the `.cs` scaffold/list/delete endpoints) — the arbitrary-source RCE surface is gone.
- CLI: `shonkor plugin install <zip> | activate <id> | deactivate <id> | list | uninstall <id>`.
  Web: `/api/plugins` (list), `POST /api/plugins/install` (ZIP upload), `.../activate`, `.../deactivate`,
  `DELETE /api/plugins/{id}` — loopback-only for state changes.
- `Security:EnablePlugins` is now an opt-OUT kill switch (default on); per-plugin activation is the gate.
- The first-party CMS parsers (Optimizely/Kentico/Sitecore) moved to a new `Shonkor.Plugin.Cms` example
  plugin project that builds an installable ZIP, instead of being compiled at runtime from embedded source.

### Changed — MCP internals: tool registry (no behavior change)
- The ~2500-line `McpRequestHandler` god-class is decomposed into an `IMcpTool` registry. Each tool is
  now a small, independently testable class under `Services/Mcp/Tools/`; shared state and helpers live in
  `McpToolContext` and `McpToolHelpers`. The handler keeps only the stdio loop and the JSON-RPC envelope
  (initialize / tools/list / tools/call), shrinking from ~2500 to ~210 lines. The tool surface and all
  outputs are unchanged; 103 tests stay green.

### Changed — MCP tool surface slimmed (34 → 26 in the local CLI)
- **`references`** replaces `impact_of`, `depends_on`, `dependency_tree`, and `blast_radius`.
  `direction` (`used_by` default / `uses`) and `depth` (1 = flat list, >1 = transitive blast
  radius with `[test]` flags / dependency tree) select the behavior.
- **`freshness`** replaces `is_fresh` + `stale_files`: with a `path` it checks one file, without
  it returns the project-wide drift report.
- **`record`** replaces `record_decision` / `record_milestone` / `record_task` / `record_question`;
  `type` selects which (decision needs `content`, milestone needs `status`).
- **`search_semantic`** is now capability-gated: it is only listed when an embedding backend is
  wired (web server + Ollama), so the local stdio CLI no longer advertises an inert tool.
- **`set_project`** now switches the active project for the current session only (in-memory). It no
  longer writes the shared, persisted `ActiveProjectName`, so switching in one chat can never
  affect another session or client.

### Changed
- The repo-root `Directory.Build.props` is now the single source of truth for the
  assembly/package version. The MCP server reports this version at runtime in the
  `initialize` handshake instead of a separate hardcoded string.
- The MCP `initialize` handshake now echoes the client's requested `protocolVersion`
  (falling back to `2025-06-18` when none is sent) instead of a fixed protocol date.

### Fixed
- `generate_capsule` now advertises the optional `projectName` argument in its tool
  schema, matching every other tool (the argument was already honored by the
  implementation but was missing from the schema).

### Removed
- Deleted the untracked local `scratch/` throwaway projects (never part of the
  source tree, the solution, or the git history).
