# BUG-008 — Plugin registry is completely wiped on a transient read error

**Severity:** High · **Status:** Confirmed · **Area:** Plugins · **Data loss**

## Context

`PluginRegistry.Load()` swallows all exceptions and returns an empty list ([PluginRegistry.cs:261-273](../src/Shonkor.Infrastructure/Services/PluginRegistry.cs)). Write paths like `InstallFromZip` (lines 133-134) do `Load().Where(…).Append(entry)` and `Save(updated)` — if the file was just locked (AV, backup, second instance), the next Save persists the empty state: **all installed plugins vanish from the registry**. Aggravating: `Save()` writes non-atomically (`File.WriteAllText`, line 278), and `AssemblyPluginLoader.LoadActive` builds its **own** registry instance with its own lock ([AssemblyPluginLoader.cs:71](../src/Shonkor.Infrastructure/Services/AssemblyPluginLoader.cs)) — interleaving of two instances on the same file is possible.

## Reproduction

Lock `registry.json` with an exclusive handle (e.g. a test process), install a plugin during that time → the registry afterwards contains only the new plugin.

## Fix

1. In `Load()`, distinguish "file missing" (→ empty list is correct) from "read failed" — the latter throws, and the operation fails cleanly.
2. Write atomically: temp file + `File.Replace`.
3. Inject a single shared registry instance (or a cross-process/cross-instance `Mutex` keyed on the registry path).

## Acceptance Criteria

- [ ] A locked/half-written registry file leads to a failure of the running operation, never to data loss.
- [ ] A crash mid-write leaves a valid (old) registry.
- [ ] Parallel test: Install + MarkFailed from two instances loses no entries.

## Definition of Done

- Fix + tests merged.
