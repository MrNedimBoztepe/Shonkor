# Konzept: JavaScript / TypeScript / Next.js / Alpine als Shonkor-Plugin-Familie

> Status: Konzept (noch nicht implementiert). Entscheidung 2026-06-25.
> Schwesterkonzept zu [PHP-Plugin-Familie](./php-analysis-plugins.md) — Adapter-/Sidecar-/Packaging-
> Mechanik dort ausführlich; hier nur die JS/TS-Besonderheiten.

---

## 1. Ist-Stand & Messlatte

Der bestehende `JavaScriptParser` ist **flacher als selbst der PHP-Parser**:

- Nutzt **Esprima.NET** (deprecated, durch Acornima abgelöst), reiner JS-Parser.
- Erzeugt **einen** `JSComponent`-Node pro Datei (ganze Datei = ein Knoten).
- Extrahiert **nur `IMPORTS`-Kanten**; keine Funktionen/Klassen/Komponenten/Exports/Calls/Typen.
- TS-„Support" = `Tolerant`-Modus → Typannotationen lassen den Parse scheitern, Datei wird
  **stillschweigend übersprungen**. `.ts`/`.tsx` damit unzuverlässig.
- Modulauflösung = naives Relativpfad-`Path.Combine` (kein tsconfig, keine Aliase, keine `exports`).

Bar heute = Datei-Knoten + Import-Kanten. **Kein Stufe 1, kein Stufe 2.** Ziel ist dieselbe
Zwei-Stufen-Tiefe wie C# (siehe PHP-Konzept §1).

## 2. Der entscheidende Unterschied zu PHP

PHP hat keinen kanonischen statischen Type-Checker. **TypeScript hat ihn: die TypeScript Compiler API
ist das „Roslyn von JS/TS"** — von Microsoft gepflegt, treibt VSCode, vollwertiger Type-Checker mit
Symbolauflösung, Cross-File-Binding und **nativer tsconfig-Modulauflösung** (paths, baseUrl,
package.json `exports`). Versteht JS *und* TS *und* JSX/TSX; inferiert via JSDoc/`checkJs` sogar
untypisiertes JS. **`ts-morph`** ist der ergonomische Wrapper (BetterReflection-Äquivalent).

Konsequenz: **Stufe-2-Parität (`CALLS`/`REFERENCES_TYPE`/`IMPLEMENTS`, typ-aufgelöste `IMPORTS`) ist für
getypten TS-Code besser erreichbar als bei PHP.** Die Dynamik-Decke bleibt real, ist aber niedriger und
**skaliert mit dem Typisierungsgrad**: voll getyptes TS ≈ nahe C#; plain untyped JS mit
`any`/dynamic `require()`/`eval` ≈ PHP-Niveau.

## 3. Tool-Bewertung

| Tool | Stufe | Urteil |
|---|---|---|
| **TS Compiler API + ts-morph** | 1 **+ 2** | **Gewählt** für den Kern. Echter Type-Checker, native Modulauflösung, first-party. Läuft in Node → Sidecar. |
| Esprima.NET (aktuell) | 1 (teilw.) | Ablösen — deprecated, kein TS, keine Typen. |
| Acornima (.NET) | 1 | In-Process-Struktur (ES2023+JSX), **kein** Type-Checker. Höchstens Fallback ohne Node. |
| Jint (.NET) | – | JS-*Interpreter* → falsches Werkzeug (wie PeachPie bei PHP). |
| tree-sitter (ts/tsx) | 1 | Syntaktisch, native Deps pro RID. Nur für **Alpine**/eingebettetes JS sinnvoll. |
| SWC / esbuild / Babel | 1 | Schnell, syntaktisch, kein Type-Checker — kein Mehrwert ggü. ts-morph hier. |

## 4. Gewählter Ansatz

Gleiches Muster wie PHP: dünner .NET-Adapter + **Node-Sidecar** (Bring-your-own Node-Runtime,
Discovery + Versions-Check ≥ 18/20), npm-Deps in `node_modules/` gevendort, hash-verifiziertes
Payload via `checksums.sha256` (Host verifiziert nur die Entry-DLL).

- **Stufe 1 — `TsFileParser : IFileParser`** (pro Datei): Sidecar-Daemon → Module/Klassen/Funktionen/
  React-Komponenten/Exports als Nodes + `CONTAINS`/`EXTENDS`/`IMPLEMENTS`-Kanten.
- **Stufe 2 — `TsSemanticLinker : IGraphPostProcessor`** (projektweit): Projektwurzel + tsconfig aus den
  Node-`FilePath`s, Sidecar im Projekt-Modus via TS Compiler API/ts-morph → `CALLS`/`REFERENCES_TYPE`/
  `IMPLEMENTS` + typ-aufgelöste `IMPORTS` + Diagnostics.
- **TS-Version:** **projekt-lokales `typescript` aus `node_modules` bevorzugen** (ts-morph kann gezielt
  laden), sonst gebündelte Version — entschärft Syntax-Drift.

## 5. Plugin-Familie

Nur **ein** Plugin parst JS/TS strukturell (sonst doppelte Nodes); Frameworks rein additiv und über
`IGraphView`-Namenslookup angebunden (wie `CrossTechLinker`).

| Plugin | Rolle | Node-Runtime? |
|---|---|---|
| **`shonkor-typescript`** (Basis) | `IFileParser` `.ts/.tsx/.js/.jsx` (Stufe 1) + `IGraphPostProcessor` (Stufe 2 via TS Compiler API/ts-morph) | **Ja** — trägt den Sidecar |
| **`shonkor-nextjs`** (spätere Welle) | additiv: Route-Graph, Server/Client-Boundary, Server-Actions, API-Routes | **Nein** (Dateisystem + JSON-Manifeste, .NET-seitig) |
| **`shonkor-alpine`** (spätere Welle) | additiv: HTML-Attribut-Direktiven (`x-data`/`x-on:`/`x-bind:`…) via AngleSharp + Ausdrucks-Parser | **Nein** — reines .NET |

**Cross-Tech:** Alpine lebt in HTML-Attributen, oft **innerhalb von Twig/Blade-Templates** → selber
Dateiraum wie der PHP-Stack. `shonkor-alpine` ist das „Smarty/Twig des JS-Universums".

**Entschiedener Scope (1. Welle):** **nur `shonkor-typescript`** end-to-end. `nextjs`/`alpine` danach;
Vue/Svelte-SFC (`.vue`/`.svelte`) noch später als eigene Plugins.

## 6. Next.js-Routengraph (für die spätere `shonkor-nextjs`-Welle)

**Entschieden: beides.**
- **Default — Dateisystem-Konventionen:** Routen aus App-/Pages-Router-Dateien (`page.tsx`,
  `layout.tsx`, `route.ts`) ableiten. Führt KEINEN Projektcode aus, kein Build nötig.
- **Opt-in — `.next/`-Build-Manifeste:** aufgelösten Routen-Graph aus `next build`
  (`routes-manifest.json`, `app-paths-manifest.json` …) lesen — exakt, aber setzt einen Build voraus,
  der Projektcode ausführt. UI-gekennzeichnet (analog zum Symfony-`bin/console`-Hinweis).

## 7. JS/TS-spezifische Risiken (über PHP hinaus)

| Risiko | Schwere | Gegenmaßnahme |
|---|---|---|
| **Modulauflösungs-Hölle** (tsconfig paths, monorepo-Workspaces, ESM/CJS, webpack/vite/next-Aliase) | **hoch** | TS-Compiler löst tsconfig nativ; Bundler-Aliase zusätzlich aus Config einlesen. Naiv-Resolver untauglich. |
| **`node_modules`** (100k+ Dateien) | hoch | per Default ausschließen; `.d.ts` nur zum Typauflösen lesen, keine externen Nodes (wie C#-Metadata → übersprungen) |
| **TS-Versions-Drift** (neue Syntax ~alle 3 Monate) | mittel | projekt-lokales `typescript` bevorzugen, sonst gebündelte Version |
| **Komponenten-/SFC-Modelle** (Vue/Svelte) | mittel | v1 = JS/TS + React/JSX; Vue/Svelte als spätere eigene Plugins |
| **Next-Manifest-Lesen führt Projektcode aus** (`next build`) | mittel | FS-Default sicher; Manifest opt-in, UI-gekennzeichnet |
| **Esprima-Ablösung** | niedrig | Basis-Plugin löst den In-Host-`JavaScriptParser` ab |

## 8. Was aus dem PHP-Konzept gilt (carry-over)

Bring-your-own-Runtime · drei getrennte Plugins · additive Framework-Post-Processor · dünner Adapter +
Sidecar · Daemon-/Projekt-Modus-Protokoll · `checksums.sha256`-Selbstprüfung · Idle-Self-Termination
gegen Zombie-Prozesse · PHP/Node-Pfad-Config in ATLAS Settings → Plugins · Node-IDs eigenes Schema,
Frameworks lösen per Graph-Namenslookup auf. Details siehe
[PHP-Konzept §4–§10](./php-analysis-plugins.md).

## 9. Build-Reihenfolge

1. **`shonkor-typescript` (Basis)** — Node-Sidecar (TS Compiler API + ts-morph → JSON) + `TsFileParser`
   (Stufe 1) + `TsSemanticLinker` (Stufe 2) + BYO-Node-Discovery + projekt-lokale-tsc-Präferenz. Löst
   den Esprima-`JavaScriptParser` ab.
2. **`shonkor-nextjs`** — additiv: Routen (FS + opt-in Manifest), Server/Client-Boundary, Actions, API.
3. **`shonkor-alpine`** — additiv: HTML-Attribut-Direktiven (AngleSharp).

Gemäß „eins fertig vor dem nächsten": Basis end-to-end, dann die Framework-Wellen.

## 10. Explizit außerhalb des Scopes (v1)

- Vue/Svelte/Angular-SFC (spätere eigene Plugins).
- Gebündelte Node-Runtime / Container-Sidecar (BYO entschieden).
- Bundler-Ausführung zur Graph-Gewinnung (nur Next-Manifest opt-in, sonst statisch).
- Inkrementelle Drift-Reconciliation für JS/TS (später).
