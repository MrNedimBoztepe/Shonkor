# BUG-014 — C#-Typ-ID-Kollisionen: gleichnamige Typen in einer Datei werden zu einem Knoten verschmolzen

**Schweregrad:** Hoch · **Status:** Bestätigt · **Bereich:** Node-ID-Schema / C#-Parser

## Kontext

`CsharpNodeId.ForType = "{filePath}::{typeName}"` ([CsharpNodeId.cs:33](../src/Shonkor.Core/Services/CsharpNodeId.cs)) trägt weder Namespace noch Generik-Arity noch die Nesting-Kette. Kollisionen:

- `namespace A { class C {} } namespace B { class C {} }` in einer Datei → ein Knoten.
- `class Foo {}` + `class Foo<T> {}` (Identifier.Text verliert die Arity) → ein Knoten.
- Zwei Klassen mit gleichnamiger nested `class Builder` → ein Knoten; Member-IDs kollidieren transitiv ([RoslynAstParser.cs:152](../src/Shonkor.Core/Services/RoslynAstParser.cs) nutzt nur den innersten Typnamen).

Letzter Upsert gewinnt → falsche Call-Hierarchien, falsche Impact-/Rename-Ergebnisse; Inhalt/Zeilen des einen Typs überschreiben den anderen. Die `CsharpNodeId`-Remarks dokumentieren nur die Partial-Type-Restambiguität — diese Kollisionen sind undokumentiert.

Verwandt (Mittel, BUG-034): Record-Primärkonstruktoren erzeugen inkonsistente Ctor-IDs zwischen `RoslynSemantics.ToNodeId` und dem Parser → hängende `CALLS`-Kanten. Beim Schema-Umbau mitlösen.

## Reproduktion

Fixture-Datei mit `namespace A { class C {} } namespace B { class C {} }` indizieren → `search_graph C` liefert einen Knoten statt zwei.

## Fix

ID um Nesting-Kette + Generik-Arity (und idealerweise Namespace) erweitern, z. B. `{file}::{Namespace}.{Outer+Inner}`{`n}`; identische Ableitung in `RoslynSemantics.ToNodeId` (via `ContainingType`-Walk). **`SchemeVersion` bumpen** (Graphen unter altem Schema werden beim Öffnen als stale erkannt → Re-Index-Empfehlung greift automatisch).

## Akzeptanzkriterien

- [ ] Die drei Kollisionsfälle oben erzeugen jeweils getrennte Knoten mit korrekten Membern.
- [ ] Parser- und Semantik-Seite erzeugen für dieselben Symbole identische IDs (Roundtrip-Test).
- [ ] `SchemeVersion` erhöht; Alt-Graphen melden Stale-Zustand.

## DoD

- Fix + Tests gemerged; Re-Index zwingend, im CHANGELOG dokumentiert.
