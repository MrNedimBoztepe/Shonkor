# BUG-013 — PHP parser: `metadata.php` produces phantom `EXTENDS` edges from every `'k' => 'v'` pair

**Severity:** High · **Status:** Confirmed · **Area:** Parser (PHP/OXID)

## Context

`MetadataExtendPattern` (`['"](\w+)['"]\s*=>\s*['"]([^'"]+)['"]`, [PhpModuleParser.cs:29](../src/Shonkor.Core/Services/PhpModuleParser.cs)) is applied to the **whole file** (line 148) instead of only to the `'extend'` array. A normal OXID `metadata.php` (`'id'`, `'title'`, `'author'`, `'templates'`, `'settings'`, …) produces dozens of bogus edges like `My Module EXTENDS title` — module dependency and impact queries are flooded with garbage.

In the same pass: `^\s*class\s+(\w+)\s+extends\s+(\w+)` (line 21) misses `abstract class`/`final class` and namespaced base classes (`\w+` stops at the `\`) — exactly the base-class layer of the OXID module chains. The Smarty block regex (line 36) requires double quotes and no extra attributes.

## Reproduction

Index a standard OXID module with `metadata.php`; `references` on the module node → `EXTENDS` edges to `title`, `author`, etc.

## Fix

First isolate the `'extend' => [ … ]` block (balanced-bracket slice), apply the pair pattern only within it. Class regex: `^\s*(?:final\s+|abstract\s+)*class\s+(\w+)\s+extends\s+([\w\\]+)`. Smarty: allow single/double quotes + optional attributes.

## Acceptance Criteria

- [ ] A fixture `metadata.php` with `id/title/templates/settings/extend` produces edges only for the `extend` entries.
- [ ] `abstract class X extends oxArticle` and `class Y extends \OxidEsales\...\Article` produce a node + edge.
- [ ] Existing cases (simple `class A extends B`) unchanged.

## DoD

- Fix + fixture tests merged; re-index note for PHP projects.
