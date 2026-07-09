# TICKET-204 – Volle Methoden-Bodies speichern + `signature`-Property emittieren

**Schweregrad-Bezug:** K2, M9 · **Aufwand:** S · **Risiko:** niedrig × niedrig (DB wächst; Voll-Re-Index nötig)

## Kontext
`RoslynAstParser.GetTruncatedContent` (`RoslynAstParser.cs:462-470`) kappt Method-/Constructor-Content auf 500 Zeichen. Folgen: `EmbeddingTextBuilder` (MaxBodyChars 1500, Head+Tail aus TICKET-105) sieht nie mehr — das Tail-Fenster ist für C# tot; FTS kann die zweite Methodenhälfte nicht matchen; `get_source` bevorzugt den gespeicherten (gekürzten) Body vor dem Datei-Slice (`ReadTools.cs:91-94`). Zusätzlich liest `EmbeddingTextBuilder.cs:39` `Properties["signature"]`, aber kein Parser schreibt den Key; Klassen-Knoten haben gar keinen Content (`RoslynAstParser.cs:323-332`).

## Akzeptanzkriterien
- [ ] Method-/Constructor-Knoten speichern den vollen Body (`ToFullString()`), Bounding passiert ausschließlich in `EmbeddingTextBuilder`.
- [ ] `EndLine` wird für Methoden/Konstruktoren gesetzt.
- [ ] `get_source` fällt auf `TryReadSourceSlice` zurück, wenn `Content` mit einem Truncation-Marker endet (Übergangsfall Bestands-DBs).
- [ ] Alle Roslyn-Symbolknoten erhalten `Properties["signature"]` (Modifier + Rückgabetyp + Name + Parameterliste).
- [ ] Class/Interface/Record/Struct-Knoten erhalten ein Member-Signatur-Skelett als `Content`.
- [ ] Test: Methode > 500 Zeichen → FTS-Treffer auf einen String aus der zweiten Hälfte; `get_source` liefert den vollständigen Body.

## Betroffene Bereiche
`RoslynAstParser.cs`, `EmbeddingTextBuilder.cs` (nur Verifikation), `ReadTools.cs`, Tests.

## Abhängigkeiten
Keine. Nach Merge: Voll-Re-Index der Bestandsdatenbanken (mit TICKET-207/208 bündeln). Effekt via TICKET-202-Suite messen (NL-Retrieval sollte steigen).

## Definition of Done
Tests grün; Bench Vorher/Nachher dokumentiert; DB-Größenzuwachs auf Shonkor selbst gemessen und im PR vermerkt.
