# BUG-009 — MCP-Proxy verschluckt Transport-Exceptions → Host hängt unbegrenzt

**Schweregrad:** Hoch · **Status:** Bestätigt · **Bereich:** CLI / MCP-Proxy

## Kontext

Im Exception-Pfad von `McpProxyClient` wird nur nach stderr geloggt, aber **keine** JSON-RPC-Antwort auf stdout geschrieben ([McpProxyClient.cs:162-165](../src/Shonkor.CLI/McpProxyClient.cs)) — der MCP-Host wartet ewig auf die Antwort zur Request-ID; typische Clients blockieren damit die ganze Konversation. Der Nicht-2xx-Zweig (Zeilen 139-159) synthetisiert korrekt einen Fehler. Betroffen sind Netzwerkfehler, DNS — und vor allem der **Default-`HttpClient.Timeout` von 100 s**, den langsame Tools (`audit`, `generate_capsule` auf großen Graphen) real reißen.

## Reproduktion

Backend stoppen (oder ein Tool > 100 s laufen lassen), `tools/call` über den Proxy senden → Host erhält nie eine Antwort.

## Fix

Im `catch`: die `id` aus der Anfragezeile parsen und eine `-32603`-Fehlerantwort (mit knapper Ursache) auf stdout emittieren — gleiche Mechanik wie der HTTP-Fehler-Zweig. `httpClient.Timeout` konfigurierbar machen bzw. deutlich erhöhen.

## Akzeptanzkriterien

- [ ] Jede Anfrage mit `id` erhält garantiert genau eine Antwort — auch bei Timeout, DNS-Fehler, Verbindungsabbruch.
- [ ] Notifications (ohne `id`) erzeugen weiterhin keine Antwort.
- [ ] Test: Backend nicht erreichbar → wohlgeformter `-32603` auf stdout.

## DoD

- Fix + Test gemerged; Timeout-Konfiguration dokumentiert.
