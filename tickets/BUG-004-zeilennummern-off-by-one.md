# BUG-004 — Alle C#-Zitate um eine Zeile daneben (0-basierte StartLine als 1-basiert ausgegeben)

**Schweregrad:** Hoch · **Status:** Bestätigt · **Bereich:** Parser / Tool-Ausgabe

## Kontext

`RoslynAstParser` speichert `StartLinePosition.Line` (0-basiert) in `GraphNode.StartLine` ([RoslynAstParser.cs:128,163,198,234,329](../src/Shonkor.Core/Services/RoslynAstParser.cs)). Alle Ausgabe-Stellen (`signature`, `locate`, `find_usages`, `edit_plan`, `implementations_of`, CLI) drucken den Wert roh als `file:line` — Leser interpretieren das 1-basiert. `CSharpDiagnostics.cs:90` rechnet dagegen explizit `+ 1 // 1-based for humans/agents`; nur `TryReadSourceSlice` ([McpToolContext.cs:109-123](../src/Shonkor.Infrastructure/Services/Mcp/McpToolContext.cs)) behandelt die Werte korrekt 0-basiert. Jede Zeilenangabe für C#-Symbole zeigt eine Zeile über die echte Deklaration — bei einem „präzisen" Graph-RAG die Kernwährung.

## Reproduktion

`signature` für eine bekannte Klasse aufrufen, ausgegebene Zeile mit der Datei vergleichen → um 1 zu niedrig.

## Fix

Eine Konvention festlegen und auf `GraphNode.StartLine`/`EndLine` dokumentieren. Empfehlung: **1-basiert speichern** (`Line + 1` in den Parsern), `TryReadSourceSlice` auf `-1` umstellen. Alle Parser prüfen (JS/GraphQL/Markdown — Markdown setzt heute gar keine Zeilen, siehe BUG-055). Kein `SchemeVersion`-Bump nötig (IDs unverändert), aber ein Re-Index ist für korrekte Werte erforderlich.

## Akzeptanzkriterien

- [ ] `signature`/`locate`/`find_usages` geben exakt die Deklarationszeile aus (Stichproben-Test über bekannte Symbole).
- [ ] `get_source`-Fallback (`TryReadSourceSlice`) liefert weiterhin den korrekten Ausschnitt.
- [ ] Konvention als XML-Doc auf dem Model dokumentiert; Test, der Parser-Output gegen eine Fixture-Datei mit bekannten Zeilen prüft.

## DoD

- Fix + Tests gemerged; Hinweis im CHANGELOG, dass ein Re-Index empfohlen ist.
