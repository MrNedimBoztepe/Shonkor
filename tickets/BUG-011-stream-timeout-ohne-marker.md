# BUG-011 — Streaming-Antworten werden nach 2 Minuten hart abgebrochen, ohne Unvollständigkeits-Marker

**Schweregrad:** Hoch · **Status:** Bestätigt · **Bereich:** LLM-Integration / RAG

## Kontext

`_httpClient.Timeout = TimeSpan.FromMinutes(2)` ([OllamaSemanticAnalyzer.cs:34](../src/Shonkor.Infrastructure/Services/OllamaSemanticAnalyzer.cs)) gilt auch für das Lesen des Response-Bodys — auch mit `ResponseHeadersRead`. Eine RAG-Generierung > 120 s (großes Kapsel-Kontextfenster + 7B-Modell auf CPU) wird mitten im Stream abgebrochen. Der Abbruch kommt als `TaskCanceledException` aus `ReadLineAsync` (Zeile 282); der Graceful-Truncation-Marker `[Antwort unvollständig]` (Zeilen 317-322) feuert nur bei `line == null`. In `SearchEndpoints.cs:186-189` sind Teil-Tokens bereits an den Client geflusht → die Antwort endet mitten im Satz, ohne Marker, mit abgebrochener Response.

## Reproduktion

Query mit großer Kapsel gegen ein langsames lokales Modell (>120 s Generierung) → Antwort bricht mid-sentence ab.

## Fix

`Timeout = Timeout.InfiniteTimeSpan` auf dem Typed Client; Connect-/First-Byte-Timeout stattdessen über `SocketsHttpHandler.ConnectTimeout` bzw. eine CTS bis zum ersten Token. Read-Loop so strukturieren, dass auch der Exception-Pfad den Truncation-Marker yielden kann (try um den Read, yield nach dem catch).

## Akzeptanzkriterien

- [ ] Generierungen > 2 min laufen durch (kein künstlicher Abbruch durch den Client-Timeout).
- [ ] Wird der Stream dennoch unterbrochen (Server weg, echtes Timeout), endet die Ausgabe mit dem `[Antwort unvollständig]`-Marker.
- [ ] Verbindungsaufbau zu nicht erreichbarem Ollama schlägt weiterhin schnell fehl (Connect-Timeout).

## DoD

- Fix + Test (simulierter Stream-Abbruch) gemerged.
