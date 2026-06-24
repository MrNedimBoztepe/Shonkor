# Design: 2-Phasen-Plugin-Vertrag (`IGraphPostProcessor`)

**Status:** Entwurf (Design + Auswirkungsanalyse — noch kein Code)
**Motivation:** Mehrere notwendige Features sind **dateiübergreifend** und am heutigen per-Datei-`IFileParser` nicht umsetzbar.

## 1. Problem
`IFileParser.ParseAsync(filePath, content)` ist **pro Datei, zustandslos** — es sieht nie den Rest des Graphen. Diese offenen Features brauchen aber den **ganzen Graphen**:

| Feature | Warum dateiübergreifend |
|---|---|
| **F3** Feldtyp-Bewusstsein | Feld-GUID → Template-Felddefinition (in *anderer* Datei) → Feldtyp (Droplink vs. Text) entscheidet, ob eine Referenz echt ist |
| **F7** Serialisierungs-Abdeckung | „referenziert, aber kein Item-Knoten existiert" = unaufgelöst/unserialisiert — braucht die Gesamtmenge |
| **F8** Diagnostik | unaufgelöste Referenzen / defekte Datasources — braucht den ganzen Graphen |
| **Helix-Verletzungen** | Layer von *Quelle und Ziel* einer Kante (Foundation→Feature = Verstoß) |
| **F6 `clrtype:`-Auflösung** | `clrtype:NS.Class` → echter C#-Knoten `{file}::Class` (C#-IDs sind dateibasiert, namespace-frei) |

## 2. Vorgeschlagenes Design
Eine **zweite, graph-bewusste Extension-Schicht**, die **nach** der per-Datei-Phase über den fertig assemblierten Graphen läuft und **additiv** Knoten/Kanten + **Diagnostik** erzeugt.

```csharp
// Shonkor.Core.Interfaces
public interface IGraphPostProcessor
{
    string Name { get; } // für Diagnostik/UI
    Task<GraphEnrichment> ProcessAsync(IGraphView graph);
}

public interface IGraphView // read-only, vom Host bereitgestellt
{
    GraphNode? GetNode(string id);
    IReadOnlyCollection<GraphNode> Nodes { get; }
    IReadOnlyCollection<GraphEdge> Edges { get; }
    IEnumerable<GraphNode> NodesByType(string type);
    IEnumerable<GraphEdge> EdgesFrom(string sourceId);
    IEnumerable<GraphEdge> EdgesTo(string targetId);
    IEnumerable<GraphNode> NodesByProperty(string key, string value); // z. B. C#-Typ per Simple-Name finden
}

// Shonkor.Core.Models
public record GraphEnrichment(
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges,
    IReadOnlyList<GraphDiagnostic> Diagnostics);

public record GraphDiagnostic(
    string Code,                    // "sitecore.unresolved-datasource"
    DiagnosticSeverity Severity,    // Info | Warning | Error
    string Message,
    string? NodeId = null,
    string? FilePath = null);
```

### Kernentscheidungen
- **Nur additiv (v1):** Post-Processoren dürfen **hinzufügen** (Knoten/Kanten/Diagnostik), aber **nicht** bestehende Knoten/Kanten ändern/löschen. → blast radius klein, reihenfolge-unabhängig, kann den per-Datei-Graphen nicht korrumpieren. (Falsch-Positive aus F3 werden per `RESOLVES_TO`/`REFERENCES_TYPED`-Kante + Diagnostik markiert, nicht entfernt — Entfernen ist eine bewusste v2-Fähigkeit.)
- **Phase-1-Snapshot:** Alle Post-Processoren sehen den **gleichen** Graphen aus Phase 1, **nicht** die Ausgabe anderer Post-Processoren. → keine Ordnungs-/Abhängigkeitskomplexität. (Ketten von Post-Processoren = v2.)
- **Storage-gestützte `IGraphView`:** als Abstraktion, damit der ganze Graph nicht zwingend im RAM liegen muss (SQLite-Indizes statt In-Memory-Collections) — siehe Performance.
- **Auto-Discovery:** `AssemblyPluginLoader` reflektiert zusätzlich über `IGraphPostProcessor` (wie heute über `IFileParser`). Ein Plugin-Assembly kann beides liefern.

## 3. Ausführungsmodell (im `GraphIndexScanner`)
1. Phase 1 (heute): Dateien scannen → `IFileParser` → Knoten/Kanten persistieren.
2. **Phase 2 (neu):** nach Phase 1 eine `IGraphView` über den persistierten Graphen aufbauen → jeden aktiven `IGraphPostProcessor` laufen lassen → `GraphEnrichment` mergen (additiv upsert) + Diagnostik persistieren.
3. Diagnostik über MCP-Tool/Web-UI sichtbar machen.

## 4. Feature-Mapping (validiert das Design)
- **F6 `clrtype:`-Auflösung:** für jeden `clrtype:NS.Class`-Knoten `NodesByProperty("TypeName","Class")`; bei genau 1 Treffer → Kante `clrtype:X -RESOLVES_TO-> {file}::Class`; bei mehreren → Diagnostik „ambiguous" + Low-Confidence-Kanten. **Einfachster Fall → Referenz-Implementierung.**
- **F8/F7 Diagnostik:** für jede GUID-Zielkante `GetNode(targetId)`; fehlt der Knoten → `GraphDiagnostic("sitecore.unresolved-datasource", Warning, …)`.
- **F3 Feldtyp:** SitecoreItem → `BASED_ON_TEMPLATE` → Template → dessen Feld-Items → `__Field Type`; daraus `REFERENCES`-Kanten als `REFERENCES_TYPED{fieldType}` anreichern bzw. Text-Feld-GUIDs als Diagnostik „likely-false-positive" markieren.
- **Helix-Verletzungen:** Layer von Quelle/Ziel über `BELONGS_TO_CONCEPT`; Foundation→Feature / Feature→Feature(fremd) → `VIOLATES_HELIX`-Kante + `Error`-Diagnostik.

→ Alle fünf offenen Features sind mit **einer** Erweiterung abgedeckt.

## 5. Auswirkungsanalyse

| Komponente | Änderung | Aufwand/Risiko |
|---|---|---|
| `Shonkor.Core` | neue Interfaces/Records (`IGraphPostProcessor`, `IGraphView`, `GraphEnrichment`, `GraphDiagnostic`) | S / niedrig (additiv) |
| `AssemblyPluginLoader` | zusätzlich `IGraphPostProcessor` entdecken/instanziieren | S / niedrig |
| `GraphIndexScanner` | Phase-2-Lauf + Merge + Diagnostik-Persistenz | **M / mittel** (Kern-Pfad) |
| Storage (SQLite) | Diagnostik-Tabelle; Indizes für `NodesByType`/`NodesByProperty` | M / mittel |
| MCP | `get_diagnostics`-Tool (o. ä.); neue Edge-/Node-Typen in Abfragen | S–M / niedrig |
| Web-UI | Diagnostik-Panel; neue Node-/Edge-Typen (`VIOLATES_HELIX`, `RESOLVES_TO`) | M / niedrig |
| Host-API-Version | `PluginHostApi.Version` 1.0 → **1.1** (additiv) | S / **siehe Versionierung** |
| Bestehende Plugins | **unverändert** — neues Interface ist opt-in | – / keins |

### Inkrementelles Indexieren (wichtigster Folgepunkt)
Phase 2 ist **whole-graph**. Bei inkrementellen Updates (wenige Dateien geändert) kann ein Post-Processor nicht einfach „nur die Diffs" sehen. **v1:** Phase 2 nach **jedem** Index voll neu laufen lassen (idempotent, additiv → vorherige Phase-2-Ausgabe vorher verwerfen/ersetzen). Akzeptabel für mittlere Graphen; **Skalierungsrisiko** bei sehr großen. v2: Invalidierungs-Scope pro Post-Processor.
→ **Konsequenz:** Phase-2-Ausgabe muss **kennzeichenbar** sein (z. B. `Origin=PostProcessor:{name}`), damit sie bei Re-Lauf sauber ersetzt wird, ohne Phase-1-Daten anzufassen.

### Performance/Memory
Whole-graph-Zugriff. `IGraphView` **storage-gestützt** halten (DB-Queries mit Indizes), nicht alles in den RAM. Braucht Indizes auf `type` und auf die per-`NodesByProperty` gesuchten Property-Keys.

### Versionierung (echte Lücke)
`PluginRegistry.ValidateManifest` vergleicht nur die **Major**-Version. Ein 1.0-Host würde ein 1.1-Plugin **akzeptieren**, dessen Post-Processor aber stumm **nicht** ausführen → stille Degradation. → **Minor-Vergleich ergänzen** (mind. Warnung, wenn Plugin neuere Minor verlangt als der Host bietet).

### Sicherheit
Post-Processoren bekommen **Lesezugriff auf den ganzen Graphen** (mehr als per-Datei). Gleiches Aktivierungs-gated-Trust-Modell. **Additiv-only** begrenzt den Schaden (kann bestehende Daten nicht verfälschen). Read-only `IGraphView` (keine Mutations-API) erzwingt das auf Typ-Ebene.

## 6. Alternativen (verworfen)
- **IFileParser einen Resolver-Kontext geben:** hilft nicht — ein einzelner Datei-Parse sieht andere Dateien weiterhin nicht.
- **Auflösung generisch im Host:** die Logik ist domänenspezifisch (Sitecore-Feldtypen, Helix-Regeln) → gehört ins Plugin, nicht in den Host.
- **Mutierende v1-API:** mächtiger (F3-Falsch-Positive löschen), aber ordnungsabhängig + kann Phase-1 korrumpieren → bewusst auf v2 verschoben.

## 7. Risiken & Mitigationen
| Risiko | Mitigation |
|---|---|
| Inkrementell wird teuer (whole-graph Phase 2) | additiv + idempotent, Phase-2-Ausgabe gekennzeichnet & ersetzbar; Invalidierungs-Scope als v2 |
| Speicher bei großen Graphen | storage-gestützte `IGraphView` + Indizes, keine In-Memory-Vollkopie |
| Stille Versions-Degradation | Minor-Versions-Check im Registry |
| Plugin-Bug in Phase 2 | Isolation wie bei `IFileParser` (Fehler → `Failed`, übrige laufen weiter); additiv → kein Datenverlust |
| Ambiguität bei Namens-Auflösung (clrtype, Component) | Low-Confidence-Kanten + Diagnostik statt falscher Hard-Links |

## 8. Rollout (phasiert)
1. **Vertrag + Verdrahtung:** Interfaces in Core, Loader-Discovery, `GraphIndexScanner`-Phase-2, Diagnostik-Persistenz, `get_diagnostics`. Host-API → 1.1 + Minor-Check.
2. **Referenz-Post-Processor:** `clrtype:`-Auflösung (F6) — einfachster Fall, beweist den Pfad end-to-end.
3. **Diagnostik-Features:** F8/F7 (unaufgelöste Referenzen).
4. **Komplex:** F3 (Feldtyp), Helix-Verletzungen.
5. (v2) Mutations-/Ketten-/Invalidierungs-Fähigkeiten, falls nötig.

## 9. Entschieden
- **Diagnostik-Oberfläche:** **eigener Typ (`GraphDiagnostic`) + eigenes MCP-Tool** (`get_diagnostics`) — nicht als Graph-Knoten vermischt.
- **`IGraphView`:** **storage-gestützt**, minimaler Satz indizierter Zugriffe (`GetNode`, `NodesByType`, `NodesByProperty`, `EdgesFrom/To/ByRelationship`) — keine In-Memory-Vollkopie.
- **Minor-Versions-Policy:** **graceful** — Plugin **wird geladen**, aber bei Host-Minor < Plugin-Minor eine **sichtbare Warnung** (Registry/Status), dass Post-Processor-Features inaktiv sind. Begründung: der `IFileParser`-Teil bleibt funktionsfähig; harte Ablehnung würde ihn unnötig mitverwerfen, stilles Ignorieren war der ursprüngliche Fehler.
