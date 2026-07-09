# BUG-003 — Unbekannter Projektname fällt still auf das aktive Projekt zurück (Cross-Projekt-/Cross-Tenant-Leak)

**Schweregrad:** Kritisch · **Status:** Bestätigt · **Bereich:** MCP / ProjectManager · **Security-relevant**

## Kontext

`ResolveProject` ([ProjectManager.cs:309-323](../src/Shonkor.Infrastructure/Services/ProjectManager.cs), Zeile 320: `project ??= GetActiveProject();`): Wird ein explizit angefragter Projektname nicht gefunden, gibt es keinen Fehler — es wird still das aktive Projekt verwendet. Das aktive Projekt ist global persistiert und über das Web-Dashboard änderbar.

Folgen: Tippfehler in `projectName`/`X-Project-Name` → Antworten und `record`-**Schreibzugriffe** landen im falschen Graph. SaaS-Worst-Case: Projekt eines tenant-gebundenen Keys verschwindet zwischen Auth und Query aus der Registry → Anfrage wird auf das aktive Projekt eines anderen Tenants umgeleitet (Datenleck).

## Reproduktion

`tools/call` mit `projectName="Gibtsnicht"` → Ergebnis kommt kommentarlos aus dem aktiven Projekt.

## Fix

In `ResolveProject` (bzw. an der Aufrufgrenze in `McpToolContext.GetStorageAsync`): Wenn ein Name explizit angefragt wurde und die Auflösung fehlschlägt → Fehler (JSON-RPC `-32602` mit klarer Meldung), niemals Fallback. Für tenant-gebundene Sessions zusätzlich: schlägt die Auflösung des authentifizierten Tenants fehl → harter Fehler, kein Fallback.

## Akzeptanzkriterien

- [ ] Unbekannter expliziter `projectName` → Fehlerantwort mit dem angefragten Namen; kein Zugriff auf einen anderen Graph.
- [ ] Tenant-gebundene Session, deren Projekt fehlt → Fehler, kein Fallback.
- [ ] Ohne expliziten Namen (Session ohne Bindung) bleibt das bisherige Verhalten (aktives Projekt) erhalten und wird in der Antwort benannt.
- [ ] Tests für alle drei Pfade.

## DoD

- Fix + Tests gemerged; Verhalten in der Tool-Beschreibung (`projectName`-Parameter) dokumentiert.
