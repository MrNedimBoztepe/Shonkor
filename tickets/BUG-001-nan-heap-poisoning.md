# BUG-001 — NaN vergiftet den Top-K-Heap der semantischen Suche; zweites NaN → Endlosschleife

**Schweregrad:** Kritisch · **Status:** Bestätigt (Logik) · **Bereich:** Retrieval

## Kontext

`SearchSemanticAsync` ([SqliteGraphStorageProvider.cs:390-404](../src/Shonkor.Infrastructure/Storage/SqliteGraphStorageProvider.cs)) berechnet `TensorPrimitives.CosineSimilarity` ohne NaN-Guard. Ein Null-Norm-Vektor in der DB (korrupter Blob, Modell liefert Nullvektor) erzeugt NaN (0/0):

1. `Comparer<double>` sortiert NaN unter alles → NaN wird `heap.Keys[0]` und wird nie evicted. Sobald der Heap voll ist, ist `score > NaN` für jeden echten Score `false` → Top-K degeneriert still zu „erste 4×maxResults Zeilen in Tabellenordnung".
2. Zweites NaN: `while (heap.ContainsKey(key)) key = Math.BitIncrement(key)` — `BitIncrement(NaN)` bleibt NaN → Endlosschleife, Request-Thread dauerhaft belegt (Threadpool-Starvation unter Last).

## Reproduktion

1. Node mit `Embedding = new float[768]` (alle 0) upserten.
2. `search_semantic` ausführen, bis der Heap füllt → Ranking entspricht Tabellenordnung.
3. Zweiten Nullvektor-Node hinzufügen, erneut suchen → Request kehrt nie zurück.

## Fix

Direkt nach der Score-Berechnung: `if (double.IsNaN(score)) continue;`. Zusätzlich (Defense in depth): Null-Norm-Vektoren beim Schreiben (`UpdateNodeEmbeddingAsync`/Upsert) ablehnen oder als NULL speichern.

## Akzeptanzkriterien

- [ ] Ein Nullvektor in der DB beeinflusst das Ranking der übrigen Treffer nicht.
- [ ] Zwei oder mehr Nullvektoren führen nicht zu Hang/Timeout; die Suche terminiert normal.
- [ ] Unit-Test: Datensatz mit ≥2 Null-Embeddings + regulären Embeddings → korrektes Top-K, Terminierung.

## DoD

- Fix + Regressionstest gemerged; Test läuft in CI.
- Kurzer Vermerk im CHANGELOG (Verhalten bei degenerierten Vektoren).
