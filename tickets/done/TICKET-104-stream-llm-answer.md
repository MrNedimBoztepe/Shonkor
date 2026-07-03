# TICKET-104 — LLM-Antwort streamen (Dashboard)
> **STATUS: ✅ DONE (2026-07-02)** — /api/ask/stream streamt token-weise (verifiziert per curl); Dashboard (atlas + index) konsumiert den Stream; Feature-Flag Features:StreamingAnswers. Build grün, 151 Tests grün.

**Findings:** H3 · **Risiko:** Niedrig × Niedrig · **Aufwand:** M · **Abhängigkeiten:** keine

## Kontext
Die RAG-/Analyse-Calls nutzen `stream = false` ([OllamaSemanticAnalyzer.cs:67,195](../src/Shonkor.Infrastructure/Services/OllamaSemanticAnalyzer.cs:195)); die „Ask AI"-Antwort erscheint erst nach vollständiger Generierung (HttpClient-Timeout 2 min). Schlechte wahrgenommene Latenz, Timeout-Risiko.

## Akzeptanzkriterien
- [ ] Ollama-Streaming (`stream=true`) über `IAsyncEnumerable`/Server-Sent-Events an das Dashboard; erste Token erscheinen sofort.
- [ ] Der MCP-Antwortpfad bleibt blockierend (Protokoll), aber ohne Regression.
- [ ] Fehler-/Abbruchbehandlung im Stream (Backend weg, Client trennt) sauber; kein hängender Request.
- [ ] Feature-Flag zum Abschalten (Fallback auf blockierend).

## Betroffene Bereiche
`OllamaSemanticAnalyzer` (Streaming-Variante), `/api/ask` (SSE), `wwwroot/app.js` (Stream-Consumer).

## Definition of Done
„Ask AI" streamt die Antwort token-weise; per Flag abschaltbar; keine Timeouts bei langen Antworten.
