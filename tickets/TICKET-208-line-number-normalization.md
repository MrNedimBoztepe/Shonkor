# TICKET-208 – Zeilennummern auf 1-basiert normalisieren

**Schweregrad-Bezug:** H7 · **Aufwand:** S · **Risiko:** mittel × niedrig (Off-by-one-Fixes erzeugen gern neue — Konventionstest ist Pflichtteil)

## Kontext
`GraphNode.cs:20-24` dokumentiert `StartLine`/`EndLine` als 1-basiert. `RoslynAstParser` speichert Roslyns 0-basierte `StartLinePosition.Line` (`:128,163,198,234,329`); `CSharpDiagnostics.cs:90` addiert korrekt +1, der Parser nicht; `PythonParser.cs:61` ist 1-basiert; `TryReadSourceSlice` (`McpToolContext.cs:119-123`) indiziert 0-basiert (für C# zufällig richtig, für Plugin-Knoten eine Zeile zu spät). Folge: Jede C#-Zitatangabe (`locate`, `outline`, `edit_plan`-Checklisten `[ ] file:line`) zeigt eine Zeile zu hoch; Agenten editieren an falschen Koordinaten.

## Akzeptanzkriterien
- [ ] Roslyn-Parser speichert `.Line + 1` (alle fünf Stellen + `EndLine`).
- [ ] `TryReadSourceSlice` rechnet 1-basiert → 0-basiert um; `CSharpDiagnostics` doppelt nicht mehr.
- [ ] Konventionstest über **alle** Parser/Plugins: erste Zeile einer Datei = `StartLine == 1`; ein Testfall pro Parser.
- [ ] `SchemeVersion`-Bump erwägen bzw. Re-Index-Hinweis (Bestands-DBs haben 0-basierte Werte) — **mit TICKET-213 bündeln**, damit Nutzer nur einen Voll-Reparse erleben.

## Betroffene Bereiche
`RoslynAstParser.cs`, `McpToolContext.cs`, `CSharpDiagnostics.cs`, alle Plugin-Parser (Verifikation), Tests.

## Abhängigkeiten
Koordination mit TICKET-213 (gemeinsamer SchemeVersion-Bump).

## Definition of Done
Konventionstest grün; manuelle Stichprobe: `locate` auf drei bekannte Symbole liefert exakt die Deklarationszeile; `get_source`-Slice eines Python-Knotens beginnt an der richtigen Zeile.
