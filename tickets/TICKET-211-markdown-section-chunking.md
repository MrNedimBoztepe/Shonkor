# TICKET-211 – Markdown Section Chunking + Summary in FTS + Concept Embeddings

**Severity ref:** H6, M8 · **Effort:** M · **Risk:** low × low (one-time FTS rebuild)

## Context
`MarkdownSection` nodes have neither `Content` nor `StartLine`/`EndLine` (`MarkdownHierarchyParser.cs:76-87`) — their embedding text is just the title; the section body lives solely on the file node (cap 100k characters without a marker, `GraphIndexScanner.cs:26,163-165`). Doc retrieval therefore only works at file granularity; section citations are impossible. In addition: FTS does not index `Summary` (`SqliteSchema.cs:114-118`) — AI summaries (often the only source of intent vocabulary) are invisible to keyword search; concept nodes are never embedded (`SqliteGraphStorageProvider.cs:1392,1432-1433`).

## Acceptance Criteria
- [ ] Every `MarkdownSection` gets the content between its own and the next header (incl. `StartLine`/`EndLine` from the match offsets); code fences and tables stay intact within a section; headers inside fences are not treated as a section boundary.
- [ ] Oversized sections (> ~4k characters) are split at paragraph boundaries into numbered sub-nodes.
- [ ] The file-node content cap gets an explicit truncation marker.
- [ ] `NodesFts` extended with `Summary` (schema migration + trigger + rebuild).
- [ ] Concept nodes are embedded (name + connected node names as the document).
- [ ] Golden-set extension (TICKET-202): ≥ 10 doc-intent cases with an expected section; Recall@10 measurably improved over the baseline.

## Affected Areas
`MarkdownHierarchyParser.cs`, `GraphIndexScanner.cs`, `SqliteSchema.cs`, `EmbeddingTextBuilder.cs`, bench golden sets.

## Dependencies
TICKET-202 (measurability). Re-index required.

## Definition of Done
Doc cases green in the bench; `get_source` on a MarkdownSection returns the section body with the correct line range; FTS finds summary vocabulary.
