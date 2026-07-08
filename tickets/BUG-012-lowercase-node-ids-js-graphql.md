# BUG-012 — JS-/GraphQL-Parser erzeugen kleingeschriebene Node-IDs → ganze Kantenfamilien hängen ins Leere

**Schweregrad:** Hoch · **Status:** Bestätigt · **Bereich:** Parser (JS/TS, GraphQL)

## Kontext

`JavaScriptParser` ([JavaScriptParser.cs:48,119](../src/Shonkor.Core/Services/JavaScriptParser.cs)) und `GraphQLParser` ([GraphQLParser.cs:48](../src/Shonkor.Core/Services/GraphQLParser.cs)) bilden Node-IDs mit `filePath.ToLowerInvariant()`; der Scanner erzeugt File-Nodes mit Original-Case-Pfad ([GraphIndexScanner.cs:169](../src/Shonkor.Infrastructure/Services/GraphIndexScanner.cs)). `Nodes.Id` ist case-sensitiv:

- Windows-Pfade (`C:\Projects\…`): **alle** `IMPORTS`- und `DEFINED_IN`-Kanten zeigen auf IDs ohne Knoten — die JS/GraphQL-Teilgraphen sind strukturell tot.
- Komplett kleingeschriebene Pfade (Linux): Komponenten-ID kollidiert mit der File-Node-ID; der Gewinner ist wegen `ConcurrentBag`-Reihenfolge nichtdeterministisch — verliert der File-Node, ist sein `ContentHash` weg und die Datei wird bei jedem Scan neu indiziert.

Verwandt (im selben Zug fixen): relative Imports ohne Extension-/Index-Auflösung (`./Button` ≠ `Button.tsx`, Zeilen 133-142) und Esprima kann kein TypeScript (Imports der meisten `.ts/.tsx`-Dateien werden still verworfen, Zeilen 88-99). Hinweis: Die geplante JS/TS-Plugin-Familie (Node-Sidecar) ersetzt diesen Parser perspektivisch — bis dahin sollte der Bestandsparser aber keine toten Kanten produzieren.

## Reproduktion

Repo mit `.tsx`-Dateien auf Windows indizieren; `get_subgraph` auf einen JS-Component-Seed → `IMPORTS`-Kanten zeigen auf nicht existente Ziele.

## Fix

`ToLowerInvariant()` entfernen; kanonische Pfadform des Scanners übernehmen (gemeinsamer `PathNodeId`-Helper). Import-Auflösung: `source + {.ts,.tsx,.js,.jsx}` und `source/index.*` gegen Kandidaten proben.

## Akzeptanzkriterien

- [ ] Jede `IMPORTS`-/`DEFINED_IN`-Kante referenziert existierende Knoten (Integritätstest nach Index-Lauf über ein Fixture-Repo).
- [ ] Keine ID-Kollision zwischen Komponenten- und File-Node; `ContentHash` des File-Node bleibt über Scans stabil.
- [ ] `import './Button'` verbindet zur `Button.tsx`-File-Node.

## DoD

- Fix + Fixture-Test gemerged; Re-Index-Hinweis im CHANGELOG.
