# TICKET-105 — Semantisches Chunking für große Symbole (Embeddings)
> **STATUS: ✅ DONE (2026-07-02)** — EmbeddingTextBuilder bettet Kopf+Fuß großer Symbole ein (nicht nur Kopf); Unit-Tests. Build grün, 151 Tests grün.

**Findings:** M1 · **Risiko:** Mittel × Mittel · **Aufwand:** M · **Abhängigkeiten:** TICKET-101, TICKET-102

## Kontext
Der Embedding-Input wird hart bei 1500 Zeichen abgeschnitten ([SemanticEnrichmentService.BuildEmbeddingText](../src/Shonkor.Web/Services/SemanticEnrichmentService.cs)); große Klassen/Methoden werden nur „kopf-eingebettet", die Rumpf-Logik ist im Vektor unsichtbar.

## Akzeptanzkriterien
- [ ] Große Symbole werden in mehrere Chunks (an Member-/Blockgrenzen) eingebettet; pro Knoten mehrere Vektoren, Scoring per Max-Similarity — oder mindestens Signatur + Doc + Kopf/Fuß statt nur Kopf.
- [ ] Schema/Storage tragen Multi-Vektoren pro Knoten (oder separate Chunk-Knoten) rückwärtskompatibel.
- [ ] `Shonkor.Eval` zeigt Verbesserung bei Queries, die auf tief im Rumpf liegende Logik zielen (neue Golden-Fälle).

## Betroffene Bereiche
`SemanticEnrichmentService`, `SearchSemanticAsync`/Scoring, Schema, Eval-Golden-Set.

## Definition of Done
Große Symbole sind über ihren gesamten Rumpf auffindbar; per Eval belegt; abwärtskompatible Migration.
