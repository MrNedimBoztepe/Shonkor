# TICKET-213 – Kanten-Kanonisierung: implementations_of, Phantom-Hubs, JS-Imports, Id-Schema

**Schweregrad-Bezug:** H4, H5, M4, M6, M15 · **Aufwand:** L · **Risiko:** mittel × mittel (Id-Schema-Änderung = SchemeVersion-Bump + Voll-Reparse; Graph-Diff-Test vor Merge)

## Kontext
1. **Zwei IMPLEMENTS/EXTENDS-Repräsentationen:** Parser targeted Namen (`RoslynAstParser.cs:345-356`), Linker targeted Node-Ids (`SemanticCsharpLinker.cs:206-219`). `implementations_of` fragt **nur** die Name-Form ab (`AnalyzeTools.cs:357-360`), aber `RelinkFilesAsync` löscht beide Formen — auch für nie re-geparste Referenzer-Dateien (`GraphIndexScanner.cs:484-489`) — und re-emittiert nur Id-Targets: jeder `reindex_file` erodiert das Tool-Ergebnis kumulativ.
2. **Phantom-Hubs:** Parser-Basistyp-Targets sind Display-Strings (`IRepository<Foo>`), Klassifikation per „I+Großbuchstabe"-Heuristik (`:476-477`), gestempelt Extracted; die Subgraph-CTE expandiert über nicht-existente Endpunkte (`SqliteGraphStorageProvider.cs:471-482`) → gleichnamige Basen namespaceübergreifend 2 Hops voneinander.
3. **JS-Imports verbinden nie:** keine Extension-/Index-Resolution (`JavaScriptParser.cs:114-142`) — `./Button` ≠ `button.tsx`-Id; Package-Namen (`react`) werden geteilte Phantom-Hubs. Intra-Projekt-JS-Graph faktisch leer.
4. **Id-Schema:** Generics-Arity fehlt (Kollision `Foo`/`Foo<T>`, `CsharpNodeId.cs:33`), Partials hängen an beliebigem Teil (`RoslynSemantics.cs:67`), Overload-Span-Ids churnen bei Edits darüber (`CsharpNodeId.cs:47-50`) und der `TypeReferences`-Relink (nur Typnamen) verfehlt Chained-Calls/using-static → hängende CALLS-Kanten.
5. **Traversal:** kein Hub-Damping/Provenance-Filter (`find_path` routet über `BELONGS_TO_MODULE`/`RELATES_TO`); OR-Join der CTE verhindert beide Edge-Indizes. XmCloud-Komponenten name-keyed über Dateigrenzen (`SitecoreXmCloudPlugin.cs:233`); `NormalizeName` strippt „controller" überall (`CrossTechLinker.cs:47-52`).

## Akzeptanzkriterien
- [ ] Id-Target ist die kanonische IMPLEMENTS/EXTENDS-Form; `implementations_of` löst zuerst den Interface-/Basis-Knoten auf und fragt eingehende Id-Kanten ab; Fallback auf Name-Kanten nur für nicht-semantische Graphen.
- [ ] Parser-Basistyp-Kanten nutzen den Simple-Name (`ExtractSimpleTypeName`), sind `Inferred` gestempelt und werden im Semantic-Mode unterdrückt (Linker liefert die exakten).
- [ ] CTE expandiert nicht über Endpunkte ohne Nodes-Eintrag; optionaler Relationship-/Provenance-Filter + Fan-out-Cap als Parameter von `GetSubgraphAsync`; CTE-Join als zwei UNION-Branches (Indexnutzung).
- [ ] JS-Import-Resolution: Extension-Probing (`.tsx/.ts/.jsx/.js`, `/index.*`) gegen die Kandidaten-Dateimenge; Package-Imports als `npm:<name>` genamespaced und vom Traversal-Default ausgeschlossen; unresolved = `Inferred`.
- [ ] Typ-Ids tragen Generics-Arity; Overloads nutzen Ordinal statt Span; Partials kanonisieren deterministisch (lexikographisch kleinste Datei) in Parser **und** `RoslynSemantics.ToNodeId`.
- [ ] Relink berücksichtigt zusätzlich Dateien mit CALLS-Kanten auf alte Node-Ids der geänderten Datei (Prefix-Query über `idx_edges_target`).
- [ ] XmCloud-Komponenten file-keyed; `RENDERS_COMPONENT` löst per Post-Pass mit `Inferred`/`Ambiguous`; `NormalizeName` strippt Suffixe nur am Wortende; Multi-Kandidat → `Ambiguous`.
- [ ] `SchemeVersion`-Bump (gemeinsam mit TICKET-208); Graph-Diff-Test: Voll-Index vorher/nachher auf Shonkor selbst, erwartete Kanten-Deltas explizit im PR gelistet.
- [ ] Golden-Fälle: `implementations_of`-Recall nach 5× reindex_file unverändert (Regressionstest für H4).

## Betroffene Bereiche
`RoslynAstParser.cs`, `SemanticCsharpLinker.cs`, `CsharpNodeId.cs`, `RoslynSemantics.cs`, `GraphIndexScanner.cs`, `SqliteGraphStorageProvider.cs`, `JavaScriptParser.cs`, `AnalyzeTools.cs`, `SitecoreXmCloudPlugin.cs`, `CrossTechLinker.cs`, Tests.

## Abhängigkeiten
TICKET-208 (gemeinsamer SchemeVersion-Bump), TICKET-207 (Provenance-Stempel), TICKET-202 (Messbarkeit). Größtes Ticket — ggf. in Teil-PRs (a: implementations_of+Phantom, b: JS, c: Id-Schema) mit einem finalen Bump.

## Definition of Done
Graph-Diff-Review abgenommen; `implementations_of`-Regressionstest grün; JS-Beispielprojekt zeigt echte IMPORTS-Kanten; `find_path` zwischen zwei per Modul verbundenen, sonst unabhängigen Klassen liefert „kein struktureller Pfad" statt Hub-Route.
