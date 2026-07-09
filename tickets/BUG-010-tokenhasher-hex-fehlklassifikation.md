# BUG-010 — 64-hex token misclassified as "already hashed": plaintext storage + permanent auth lockout

**Severity:** High · **Status:** Confirmed · **Area:** Auth · **Security**

## Context

`TokenHasher.LooksHashed` classifies every 64-character hex string as a digest; `EnsureHashed` then passes it through unchanged ([TokenHasher.cs:20-25](../src/Shonkor.Infrastructure/Services/TokenHasher.cs)). `ProjectManager.AddProject` accepts a **caller-supplied** API key ([ProjectManager.cs:195-201](../src/Shonkor.Infrastructure/Services/ProjectManager.cs)): a token that happens to be exactly 64 hex characters long (the most common form of a 32-byte token) is stored in **plaintext** in `projects.json` — and because `Verify` hashes the presented token (`SHA256(token) != token`), authentication for this key **always** fails, with no explanatory error.

Side finding (same module): `LooksHashed` accepts uppercase hex, `Hash` emits lowercase, `Verify` compares byte-for-byte → an uppercase stored digest never matches.

## Reproduction

Create a project/user with API key `"a" * 64` (or 64 random hex characters) → the key is in plaintext in `projects.json`; auth with exactly this key returns 401.

## Fix

Abolish shape-sniffing: store hashes self-describingly (`sha256:<hex>`); `EnsureHashed` hashes everything without a prefix and stamps the prefix; one-time migration of existing entries on load. `Verify` normalizes (`ToLowerInvariant`) before comparison.

## Acceptance Criteria

- [ ] A 64-hex plaintext token is stored hashed and authenticates correctly.
- [ ] Existing hashed entries (with/without prefix, upper/lower case) still verify.
- [ ] `projects.json` contains no plaintext secrets after migration (test via the file format).

## Definition of Done

- Fix + migration + tests merged; format change in the CHANGELOG.
