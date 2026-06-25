# Konzept: PHP / Symfony / Sylius / OXID als Shonkor-Plugin-Familie

> Status: Konzept (noch nicht implementiert). Entscheidung 2026-06-25.
> Ziel: PHP-Code-Analyse mit derselben Tiefe wie der bestehende C#-Pfad, ausgeliefert als
> **drei separat installierbare Plugins** mit allen nötigen Abhängigkeiten im Paket.

---

## 1. Ziel & Messlatte

Die bestehende C#-Tiefe entsteht in **zwei Stufen**:

1. **Pro-Datei, syntaktisch** (`RoslynAstParser`, `IFileParser`): Typen, Methoden, Properties,
   Konstruktoren als Nodes + `CONTAINS`-Kanten + Liste referenzierter Typnamen.
2. **Projektweit, semantisch** (`SemanticCsharpLinker`): echte Roslyn-`Compilation`, löst über das
   `SemanticModel` **exakte** `CALLS` / `REFERENCES_TYPE` / `IMPLEMENTS` / `EXTENDS`-Kanten auf.

Darüber liegt die sprach-agnostische Ollama-Anreicherung (Concepts, `RELATES_TO`, Summaries), die
für PHP-Nodes **gratis** mitläuft.

**„Kein Unterschied zu C#"** heißt also: PHP muss **beide** Stufen bekommen. Der heutige
`PhpModuleParser` deckt nur einen Bruchteil von Stufe 1 ab (Regex: `class X extends Y`,
OXID-`metadata.php`-`extend`, Smarty-Blöcke) — eine PHP-Stufe-2 existiert gar nicht.

> Hinweis: Eine VB-Parität existiert im Code **nicht** (kein VB-Parser vorhanden). Einzige echte
> Referenz ist C#.

## 2. Die fundamentale Decke (ehrlich)

PHP ist dynamisch typisiert. `$service->doThing()` mit `$service` aus einem DI-Container
(`$container->get('app.foo')`) oder über `__call`/`__get`-Magie ist **statisch nicht auflösbar**.
Daraus folgt hart:

- **Strukturelle Parität** (Typen, Methoden, Vererbung, Traits, Signaturen) → **erreichbar.**
- **Call-/DI-Parität** wie in C# → **durch Parsen allein nicht erreichbar.** Nur annäherbar über
  **Framework-Introspektion** (DI-Container-Dump) + ehrlich `confidence`-gelabelte Kanten sonst.

Dieses Konzept maximiert Parität, ohne diese Decke zu verschweigen.

## 3. Verworfene Ansätze

**PeachPie — verworfen.** Ist ein *Ausführungs*-Compiler (PHP→CIL), kein Analyse-Werkzeug. Verlangt
kompilierbaren Code inkl. gesamtem Composer-`vendor`-Baum; bricht an PHP-Dynamik/Magie regelmäßig.
Pinnt eigene `Microsoft.CodeAnalysis`-Versionen → **konkreter Roslyn-Versionskonflikt** mit eurem
C#-Pfad. Löst das DI-Problem ohnehin nicht. Hoher Aufwand, fragil, falsches Werkzeug.

**Tree-sitter — nur für Templates/Config.** Schnell, fehlertolerant, inkrementell, multi-Grammatik
(PHP/Twig/YAML/XML). Aber: liefert nur einen **Syntax**baum, **keine** Namensauflösung über Dateien —
PHPs `use`-Alias-/Namespace-/Trait-Logik müsste man selbst nachbauen. Plus native C-Lib → P/Invoke +
Grammatik-Binaries **pro RID**, was das xcopy-.NET-Deploy verkompliziert. Bringt nur Stufe 1; das Rad
(PHP-Semantik) hat das PHP-Ökosystem schon erfunden.

## 4. Gewählter Ansatz: dünner .NET-Adapter + PHP-Sidecar

Der Loader lädt nur **.NET-Assemblies** in-process. PHP-Verstehen passiert **out-of-process** — exakt
das Muster, das ihr für Ollama schon nutzt (Host ruft externen Prozess, bekommt JSON).

| Schicht | Inhalt | Vom Host |
|---|---|---|
| **.NET-Adapter** (`Shonkor.Plugin.Php.dll`) | implementiert `IFileParser` + `IGraphPostProcessor`, startet/steuert den Sidecar, übersetzt JSON → `GraphNode`/`GraphEdge` | **geladen** (collectible ALC, SHA-256-geprüft) |
| **PHP-Sidecar** (`sidecar/`) | der Analyzer: `nikic/php-parser` (BSD) + `roave/better-reflection` (MIT), optional `phpstan/phpstan` (MIT), als **gevendortes** Composer-Projekt | **nicht geladen** — nur entpacktes Payload, per Prozess gestartet |

Der Adapter enthält **keine** PHP-Logik (nur Marshalling + Lifecycle). Die Sprachintelligenz lebt im
Sidecar und ist unabhängig vom .NET-Build aktualisierbar.

**Warum nikic + BetterReflection:** Fundament von PHPStan/Psalm/Rector — dem gesamten
PHP-Static-Analysis-Ökosystem. BetterReflection löst Klassen/Methoden/Vererbung/Traits
**namespace-korrekt ohne Code-Ausführung** auf. Damit ist strukturelle Parität sauber erreichbar, und
für Call-/Typtiefe ist der Weg zu PHPStan offen.

### PHP-Runtime: Bring-your-own (entschieden)

Das Paket enthält **nur** Sidecar-Code + `vendor/`. Der Adapter findet das auf dem Host installierte
`php` (PATH/konfiguriert), prüft Version ≥ 8.1 und nötige Extensions (tokenizer, mbstring). Kleinstes,
plattform-universelles ZIP. Setzt PHP auf der Index-Maschine voraus (bei Symfony/OXID-Devs ohnehin
vorhanden).

- **Nicht** Runtime-pro-RID bündeln (30–50 MB/Plattform, RID-spezifische Pakete, CVE-/Update-Pflicht).
- **Nicht** Container-Sidecar als Default (PHP-Images sind Linux → kollidiert mit dem
  Docker-Windows-Container-Modus dieses Setups).

## 5. Die Drei-Plugin-Familie

Euer Plugin-Modell hat **keinen** Inter-Plugin-Abhängigkeitsmechanismus (nur `minHostApi`). Würden
mehrere Plugins `.php` strukturell parsen, entstünden **doppelte Nodes**. Lösung: nur **eines** parst
die PHP-Sprache; die anderen zwei sind **rein additive Post-Processor** (passt exakt zum
„additive only"-Vertrag von `IGraphPostProcessor`).

| Plugin | Rolle | PHP-Runtime? |
|---|---|---|
| **`shonkor-php-symfony`** (Basis) | `IFileParser` für `.php`/`.twig` (Stufe 1) + `IGraphPostProcessor` (Stufe 2: `CALLS`/`REFERENCES_TYPE` via BetterReflection) + Symfony-DI-Container-Dump | **Ja** — trägt den Sidecar |
| **`shonkor-oxid`** | nur `IGraphPostProcessor` + `.tpl`-Smarty-Parser: `metadata.php`-`extend`-Ketten, Modulstruktur, Block-Overrides | **Nein** — .NET-seitig (Regex/YAML) |
| **`shonkor-sylius`** | nur `IGraphPostProcessor`: Resource-Model, Winzou-State-Machines (YAML/XML), Grids | **Nein** — .NET-seitig (YAML/XML) |

**Eigenschaften:**
- **Unabhängig installierbar, ohne Reihenfolge-Zwang.** OXID/Sylius reichern an, *was an PHP-Nodes da
  ist*. Fehlt die Basis, finden sie nichts und emittieren nichts — sauberer Graceful-Degrade, kein
  Crash (genau das, was der additive Vertrag garantiert).
- **Nur die Basis schleppt die schwere Abhängigkeit** (PHP-Runtime + `vendor/`). Die beiden
  Framework-Plugins sind leichte, PHP-freie .NET-Pakete.
- OXID 7 läuft auf Symfony-DIC → profitiert vom Container-Dump der **Basis**; das OXID-Plugin ergänzt
  nur OXID-Spezifika.

**Kanten-Anbindung ohne Code-Kopplung:** OXID/Sylius-Post-Processor hängen ihre Kanten **nicht** über
rekonstruierte Node-IDs an, sondern über **`IGraphView`-Namenslookup** (`DefinitionsByNameAsync`) —
genau wie `CrossTechLinker` C#-Klassen per Name auflöst. Damit bleiben die drei Plugins entkoppelt.

## 6. Abbildung auf das Zwei-Stufen-Modell (Basis-Plugin)

- **Stufe 1 — `PhpFileParser : IFileParser`** (pro Datei): schickt `{filePath, content}` an den
  Sidecar-Daemon → Klassen/Interfaces/Traits/Methoden/Properties/Funktionen als Nodes +
  `CONTAINS`/`EXTENDS`/`IMPLEMENTS`/`USES_TRAIT`-Kanten. Spiegelt `RoslynAstParser`.
- **Stufe 2 — `PhpSemanticLinker : IGraphPostProcessor`** (projektweit): leitet die Projektwurzel aus
  den `FilePath`s der PHP-Nodes ab, startet den Sidecar im Projekt-Modus → `CALLS`/`REFERENCES_TYPE`
  (BetterReflection, FQN-aufgelöst) + `INJECTS`/`RESOLVES_SERVICE` aus dem DI-Container-Dump +
  Diagnostics (`php.unresolved-service`, confidence-Labels). Spiegelt `SemanticCsharpLinker`.

**Symfony-DI-Container-Dump** = der eigentliche Parität-Gewinn: `bin/console debug:container
--format=xml` bzw. der kompilierte Container liefert exakt, **welche konkrete Klasse welche Service-ID
erfüllt und was wohin injiziert wird** — die DI-Indirektion, die statische Analyse besiegt.

## 7. Paket-Layout & Manifest

```
shonkor-php-symfony-1.0.0.zip
├── plugin.json                     # von PluginRegistry validiert
├── Shonkor.Plugin.Php.dll          # entryAssembly  ← einzige SHA-256-verifizierte Datei
├── Shonkor.Plugin.Php.deps.json
└── sidecar/                        # reines Payload, NICHT in .NET geladen
    ├── bin/analyze.php             # Einstieg: Daemon- + Projekt-Modus
    ├── src/                        # Visitors, FQN-Auflösung, JSON-Emitter, Framework-Extraktoren
    ├── composer.json
    ├── composer.lock
    ├── vendor/                     # gevendort: nikic/php-parser, roave/better-reflection, (phpstan)
    └── checksums.sha256            # Integritätsmanifest → Adapter prüft beim 1. Start
```

```json
{
  "id": "shonkor-php-symfony",
  "name": "PHP / Symfony",
  "version": "1.0.0",
  "entryAssembly": "Shonkor.Plugin.Php.dll",
  "minHostApi": "1.0"
}
```

> `PluginManifest` bindet nur diese fünf Felder; Zusatzfelder verwirft System.Text.Json. PHP-Pfad/Modus
> gehören daher in eine `sidecar.config.json` bzw. (besser) in **ATLAS Settings → Plugins**.

Die `oxid`- und `sylius`-Pakete haben dasselbe Layout, aber **ohne** `sidecar/` (kein PHP, reine
.NET-Post-Processor) — nur `plugin.json` + ihre DLL + .NET-Deps.

## 8. Sidecar-Protokoll

- **Daemon-Modus** (Stufe 1): Adapter startet `php bin/analyze.php --serve` **einmal pro Index-Lauf**,
  schreibt newline-delimited JSON-Requests auf stdin, liest Nodes/Edges von stdout. Amortisiert
  PHP-Startkosten über tausende Dateien (kein Prozess-pro-Datei).
- **Projekt-Modus** (Stufe 2): `php bin/analyze.php --project <dir>` → BetterReflection +
  Container-Dump über das ganze Projekt → ein JSON-Batch.
- **Node-IDs:** eigenes FQN-Schema `php::{Fqcn}::{member}#{arity}`, deterministisch zwischen Stufe 1
  und 2 — dasselbe Problem, das C# mit `CsharpNodeId` + Overload-Span löst.

## 9. Host-seitige Voraussetzungen & offene Lücken

Strikt nötig: **keine** (Adapter kommt mit Projektwurzel-aus-FilePaths + `Process.Start` aus). Drei
kleine Erweiterungen machen es erst rund:

1. **Lifecycle-Hook:** `IFileParser`/`IGraphPostProcessor` haben **kein `Dispose`** → der
   Daemon-Prozess muss zuverlässig sterben (sonst Zombie-`php`). Lösung: Idle-Self-Termination +
   PID-Tracking im Adapter; optional ein „Index-Lauf zu Ende"-Signal vom Host.
2. **Settings-Plumbing:** PHP-Pfad/Modus in ATLAS Settings → Plugins.
3. **Payload-Integrität:** `PluginRegistry` verifiziert **nur** die Entry-DLL; der `vendor/`-Baum ist
   ungeprüft → Adapter verifiziert `checksums.sha256` vor dem ersten `php`-Aufruf selbst.

## 10. Sicherheit

- Installation bleibt **inert** — Code läuft erst nach expliziter Aktivierung (wie bei allen Plugins).
- Der Adapter führt nie beliebigen PHP-Code des Zielprojekts aus, sondern nur den gevendorten,
  hash-verifizierten Sidecar. **Ausnahme:** der Symfony-Container-Dump ruft das `bin/console` des
  Zielprojekts (führt Projektcode beim Cache-Build aus) — bewusst, opt-in, und nur im
  Projekt-Modus. Das muss in der UI klar als „führt `bin/console` des Projekts aus" gekennzeichnet
  sein.
- BYO-Runtime: kein gebündeltes `php` → keine eigene CVE-/Update-Pflicht für die Runtime.

## 11. Risiken (gewichtet)

| Risiko | Schwere | Gegenmaßnahme |
|---|---|---|
| PHP-Runtime auf Host nötig | mittel | Discovery + Version/Extension-Check, klare Settings-Fehlermeldung |
| `vendor/` nicht host-verifiziert | mittel | `checksums.sha256` + Adapter-Prüfung |
| Zombie-Daemon-Prozesse | mittel | Idle-Timeout + PID-Tracking |
| Call-Graph statisch nie C#-exakt | grundsätzlich | Container-Dump (exakt für DI) + confidence-Labels sonst |
| `bin/console`-Aufruf führt Projektcode aus | mittel | opt-in, UI-Kennzeichnung, nur Projekt-Modus |
| Doppel-Parsing bei mehreren PHP-Parsern | hoch | nur die Basis parst `.php`; OXID/Sylius rein additiv |

## 12. Build-Reihenfolge (je lieferbar)

1. **`shonkor-php-symfony` (Basis)** — Sidecar-Skelett (nikic + BetterReflection → JSON) +
   `PhpFileParser` (Stufe 1) + `PhpSemanticLinker` (Stufe 2) + BYO-Runtime-Discovery +
   Symfony-Container-Dump. *Phase 1 — Fundament für alles Weitere.*
2. **`shonkor-oxid`** — additiver Post-Processor + `.tpl`-Parser (der bestehende `PhpModuleParser`
   liefert den OXID-`metadata.php`/Smarty-Kern als Vorlage).
3. **`shonkor-sylius`** — additiver Post-Processor (YAML/XML Resource + Winzou-State-Machines).

Gemäß „eins fertig vor dem nächsten": Basis end-to-end, dann OXID, dann Sylius.

## 13. Explizit außerhalb des Scopes (v1)

- Gebündelte PHP-Runtime / Container-Sidecar (bewusst verworfen).
- Inkrementelle Drift-Reconciliation für PHP (C#-spezifisch; später).
- PeachPie / tree-sitter für den PHP-Sprachkern (verworfen; tree-sitter höchstens später für
  Templates).
- Laravel/WordPress/andere PHP-Frameworks (anderes Konzept).
