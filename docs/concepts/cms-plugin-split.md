# Konzept: `shonkor-cms` in drei Plugins aufteilen (Sitecore / Kentico / Optimizely)

> Status: **UMGESETZT** 2026-06-25 (Branch `refactor/cms-split-three-plugins`). Namespaces pro Plugin
> umbenannt, drei ZIPs gebaut, 136 Tests grün. Entscheidung 2026-06-25.
> Anders als die [PHP-](./php-analysis-plugins.md)/[JS-TS-](./typescript-analysis-plugins.md)Konzepte
> existiert dieser Code bereits als **ein** Projekt `Shonkor.Plugin.Cms` — dies ist ein **Refactor**,
> kein Greenfield-Entwurf.

---

## 1. Motivation

Heute bündelt ein einziges Plugin `shonkor-cms` (`id: shonkor-cms`, eine `Shonkor.Plugin.Cms.dll`,
ein ZIP via `PackPluginZip`-Target) drei unabhängige CMS-Welten. Wer nur Sitecore nutzt, aktiviert
trotzdem Kentico- und Optimizely-Parser mit. Aufteilen macht jede Welt separat installier-/aktivierbar
— konsistent zur PHP-/JS-Plugin-Familie.

## 2. Befund: sauberer, Sitecore-lastiger Split

Alle Komponenten liegen flach im Namespace `Shonkor.Plugins`. Es gibt **keinen geteilten Code** über
die drei CMSe hinweg (kein Basis-Lib nötig). Alle Post-Processor-`Name`s sind `sitecore.*`.

**→ `shonkor-sitecore`** (9 Klassen + Konstanten)
- Parser: `SitecoreUnicornPlugin` (`.yml/.yaml`), `SitecoreConfigPlugin` (`.config`),
  `SitecoreXmCloudPlugin`/JSS (`.tsx/.jsx/.ts/.js/.json`), `HelixSemanticPlugin`
- Post-Processor: `HelixViolationPostProcessor`, `FieldTypeReferencePostProcessor`,
  `ClrTypeResolverPostProcessor`, `UnresolvedDatasourcePostProcessor`, `SerializationCoveragePostProcessor`
- Hilfs: `SitecoreCmsConstants`

**→ `shonkor-kentico`** — `KenticoPlugin` (`.cs`)

**→ `shonkor-optimizely`** — `OptimizelyPlugin` (`.cs`)

Helix ist eine Sitecore-Architektur und gehört eindeutig zu Sitecore. `ClrTypeResolver` löst die
`ClrType`-Nodes auf, die `SitecoreConfigPlugin` erzeugt → Sitecore. Mehrere `.cs`-Parser koexistieren
schon heute (Kentico + Optimizely + Host-`RoslynAstParser`); der Scanner führt alle passenden Parser
aus und merged — der Split ändert daran nichts.

## 3. Zielstruktur

Drei Projekte ersetzen `src/Shonkor.Plugin.Cms/`:

```
src/Shonkor.Plugin.Sitecore/    → Shonkor.Plugin.Sitecore.dll   → shonkor-sitecore.zip
src/Shonkor.Plugin.Kentico/     → Shonkor.Plugin.Kentico.dll    → shonkor-kentico.zip
src/Shonkor.Plugin.Optimizely/  → Shonkor.Plugin.Optimizely.dll → shonkor-optimizely.zip
```

Jedes Projekt: 1:1 die bestehende `.csproj`-Vorlage (TargetFramework net10.0, `Private=false` auf
`Shonkor.Core`, `CopyLocalLockFileAssemblies=false`, `PackPluginZip`-Target) + eigene `plugin.json`.
Namespace pro Projekt z. B. `Shonkor.Plugin.Sitecore` / `.Kentico` / `.Optimizely` (oder flach
`Shonkor.Plugins` beibehalten — minimiert Diff; empfohlen: umbenennen für Klarheit).

### Manifeste

```jsonc
// shonkor-sitecore
{ "id": "shonkor-sitecore", "name": "Sitecore (Unicorn, XM Cloud/JSS, Config, Helix)",
  "version": "1.0.0", "entryAssembly": "Shonkor.Plugin.Sitecore.dll", "minHostApi": "1.0",
  "targetExtensions": [".yml",".yaml",".config",".json",".ts",".tsx",".js",".jsx",".xml",".html",".cshtml"] }

// shonkor-kentico
{ "id": "shonkor-kentico", "name": "Kentico (Page types, modules, form components)",
  "version": "1.0.0", "entryAssembly": "Shonkor.Plugin.Kentico.dll", "minHostApi": "1.0",
  "targetExtensions": [".cs"] }

// shonkor-optimizely
{ "id": "shonkor-optimizely", "name": "Optimizely (Content types)",
  "version": "1.0.0", "entryAssembly": "Shonkor.Plugin.Optimizely.dll", "minHostApi": "1.0",
  "targetExtensions": [".cs"] }
```

## 4. Auswirkungen (das eigentlich Wichtige bei einem Refactor)

- **Solution:** alte `Shonkor.Plugin.Cms` entfernen, drei neue Projekte aufnehmen.
- **Tests:** `GraphPostProcessorTests` (nutzt `ClrTypeResolverPostProcessor`) und ggf.
  `AssemblyPluginLoaderTests` referenzieren die Plugin-Typen direkt → Test-Projekt-Referenz auf
  `Shonkor.Plugin.Sitecore` umstellen (ClrTypeResolver/Helix liegen dort).
- **Build-Output:** statt einer `Shonkor.Plugin.Cms.zip` entstehen drei ZIPs. Etwaige CI-/Doku-
  Verweise auf den alten ZIP-Namen anpassen.
- **Migration (Breaking für bestehende Workspaces):** die Registry ist nach `id` verschlüsselt. Der
  alte `shonkor-cms` bleibt installiert, wird aber nie wieder gebaut. Nutzer müssen `shonkor-cms`
  deinstallieren und die drei neuen Plugins installieren/aktivieren. → In den Release-Notes
  dokumentieren; optional einen einmaligen Migrationshinweis in ATLAS Settings → Plugins, wenn
  `shonkor-cms` erkannt wird.
- **Verhalten:** rein additiv, keine Logikänderung — bei aktivierten Sitecore-Plugin ist der Graph
  identisch zu vorher. Nur die Paketgrenze verschiebt sich.

## 5. Risiken

| Risiko | Schwere | Gegenmaßnahme |
|---|---|---|
| Bestehende `shonkor-cms`-Installationen verwaisen | mittel | Migrationshinweis + Release-Notes; alte ID erkennen |
| Test-Referenzen brechen beim Verschieben der Typen | niedrig | Test-Projekt auf `Shonkor.Plugin.Sitecore` referenzieren; `dotnet test` als Gate |
| Namespace-Umbenennung vergrößert Diff | niedrig | optional flach `Shonkor.Plugins` lassen → minimaler Diff |
| XM-Cloud-Parser (`.ts/.tsx/.js`) überschneidet sich mit künftigem JS/TS-Plugin | niedrig | bewusst; JSS-Erkennung bleibt Sitecore-spezifisch, additiv |

## 6. Vorgehen (umsetzungsbereit)

1. `src/Shonkor.Plugin.Sitecore/` anlegen, die 10 Sitecore-Dateien verschieben, `.csproj` +
   `plugin.json` aus der Vorlage.
2. `src/Shonkor.Plugin.Kentico/` (KenticoPlugin) und `src/Shonkor.Plugin.Optimizely/`
   (OptimizelyPlugin) analog.
3. `Shonkor.Plugin.Cms` aus Solution + Repo entfernen.
4. Test-Projekt-Referenz aktualisieren; `dotnet build` + `dotnet test` grün.
5. Drei ZIPs verifizieren (Build-Output), kurz gegen ein echtes Sitecore-Projekt
   (`C:\Projects\sitecoreMuM`) installieren/aktivieren/reindexieren.

Reihenfolge gemäß „eins fertig vor dem nächsten": Sitecore zuerst (Großteil), dann Kentico, dann
Optimizely — oder, da rein mechanisch, in einem Zug mit grünem Test-Gate.
