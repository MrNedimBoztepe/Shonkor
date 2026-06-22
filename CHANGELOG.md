# Changelog

All notable changes to Shonkor are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/), and the project uses semantic versioning.

## [Unreleased]

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
