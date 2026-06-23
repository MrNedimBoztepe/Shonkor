# Sitecore-Plugin — Gap-Analyse & Roadmap

Kritische Bestandsaufnahme der Sitecore-spezifischen Parser im CMS-Plugin
(`src/Shonkor.Plugin.Cms/`), mit definierten Entwickler-Features und Folgenabschätzung.

## Ist-Zustand
- **SitecoreUnicornPlugin** (`.yml`): `SitecoreItem`-Knoten + Kanten `BASED_ON_TEMPLATE`, `HAS_CHILD`, `REFERENCES`. Brauchbare Basis.
- **SitecoreXmCloudPlugin** (`.tsx/.jsx/.json`): **Stub** — `Contains`-Heuristik, keine Kanten.
- **HelixSemanticPlugin**: existiert nur in `plugins/` (Repo-Root) als **totes, nicht-kompilierendes** Legacy (`GraphNode.Metadata`, `GraphEdge.RelationType/.Id/.FilePath`, 4-arg `NodeTypeDescriptor` — alle aus der aktuellen API entfernt). Helix-Support faktisch fallengelassen.

## Kritische Mängel
- **B0 (Headline):** `SitecoreUnicornPlugin` überspringt `__`-Felder bei der Kantenbildung → **`__Renderings`/`__Final Renderings` (Präsentation) und `__Base template` (Vererbung) fehlen** — also genau die zentralen Sitecore-Beziehungen.
- **B1:** Totes `plugins/`-Duplikat (s. o.).
- **B2:** XM-Cloud-Parser ist Platzhalter.
- **B3:** `catch (Exception) {}` schluckt YAML-Fehler still.
- **B4:** GUID-Regex ohne Feldtyp-Wissen → False-Positive-Referenzen.
- **B5:** `plugin.json targetExtensions` unvollständig (`.cs, .yml` — fehlen `.yaml/.tsx/.jsx/.json`).
- **B6:** Sitecore-Knoten ohne `Content`/`Summary` → schwach in der semantischen Suche.
- **B7:** GUID-Format inkonsistent (Unicorn-IDs lowercase/dashed vs. Layout-/Feld-GUIDs uppercase/braces) → Kanten lösen nicht auf. Test mit Debug-Cruft, XM-Cloud/Helix ungetestet.

## Architektonische Kernlücke
`IFileParser.ParseAsync(filePath, content)` ist **pro Datei, zustandslos**. Mehrere notwendige Features sind **dateiübergreifend** (Feldtyp-Auflösung, GUID→Name, Helix-Verletzungen, unaufgelöste Referenzen) → brauchen eine **2-Phasen-Vertragserweiterung** (graph-bewusster Post-Processor). Host-Entscheidung, nicht nur Plugin-Code.

## Notwendige Entwickler-Features
| # | Feature | Phase | Wert/Aufwand |
|---|---|---|---|
| F1 | Präsentations-Graph (`__Renderings` → Item→Rendering@Placeholder, Rendering→Datasource) | pro Datei | hoch / M |
| F2 | Template-Vererbung (`__Base template` → `INHERITS_FROM`) + Felder | pro Datei (+P2) | hoch / M |
| F3 | Feldtyp-Bewusstsein (echte vs. unechte Referenzen) | **P2** | hoch / L |
| F4 | XM-Cloud/JSS echt (Component/Placeholder/Route/GraphQL) | pro Datei (+P2) | hoch / L |
| F5 | Helix portieren + Verletzungs-Erkennung | pro Datei (+P2) | hoch / M |
| F6 | Config-/Patch-Graph (Pipelines/DI/Settings) | pro Datei | mittel / M |
| F7 | Serialisierungs-Abdeckung (verwaiste Items) | P2 | mittel / M |
| F8 | Diagnostik (unaufgelöste Refs, Parse-Fehler statt stillem Schlucken) | P2 | mittel / S |

## Folgenabschätzung
- **Status quo = Marketing-Häkchen:** ohne Präsentation/Vererbung beantwortet das Plugin **nicht** die Impact-Fragen, für die Sitecore-Devs das Tool wollen → Glaubwürdigkeitsrisiko bei Auslieferung als „Sitecore-Support".
- **Größter Hebel (F1/F2) geht pro Datei** → sofort machbar (umgesetzt, s. u.).
- **Hochwertige Features (F3/F5-Verletzungen/F8) hängen an der 2-Phasen-Erweiterung** → strategische Entscheidung **vor** Umsetzung, sonst pro-Datei-Mehrfacharbeit.

## Empfohlene Reihenfolge
1. **F1/F2 + B0/B7-Fix** (umgesetzt: Präsentation/Vererbung + GUID-Normalisierung + Test).
2. `plugins/`-Leiche entfernen, **Helix nach `src/` portieren** (F5-Basis).
3. **2-Phasen-Plugin-Vertrag** entwerfen → dann F3/F7/F8.
4. F4 (XM-Cloud echt), F6 (Config).

## Status
- **F1/F2 umgesetzt** in `SitecoreUnicornPlugin` (`HAS_RENDERING`, `USES_DATASOURCE`, `INHERITS_FROM`, GUID-Normalisierung) + Test. Rest offen.
