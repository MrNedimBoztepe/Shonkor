# BUG-013 — PHP-Parser: `metadata.php` erzeugt Phantom-`EXTENDS`-Kanten aus jedem `'k' => 'v'`-Paar

**Schweregrad:** Hoch · **Status:** Bestätigt · **Bereich:** Parser (PHP/OXID)

## Kontext

`MetadataExtendPattern` (`['"](\w+)['"]\s*=>\s*['"]([^'"]+)['"]`, [PhpModuleParser.cs:29](../src/Shonkor.Core/Services/PhpModuleParser.cs)) wird auf die **ganze Datei** angewendet (Zeile 148) statt nur auf das `'extend'`-Array. Ein normales OXID-`metadata.php` (`'id'`, `'title'`, `'author'`, `'templates'`, `'settings'`, …) erzeugt Dutzende Bogus-Kanten wie `My Module EXTENDS title` — Modul-Abhängigkeits- und Impact-Abfragen sind mit Müll geflutet.

Im selben Zug: `^\s*class\s+(\w+)\s+extends\s+(\w+)` (Zeile 21) verfehlt `abstract class`/`final class` und namespaced Basisklassen (`\w+` stoppt am `\`) — genau die Basisklassen-Schicht der OXID-Modulketten. Smarty-Block-Regex (Zeile 36) verlangt doppelte Anführungszeichen und keine Zusatzattribute.

## Reproduktion

Standard-OXID-Modul mit `metadata.php` indizieren; `references` auf den Modul-Knoten → `EXTENDS`-Kanten auf `title`, `author` etc.

## Fix

Zuerst den `'extend' => [ … ]`-Block isolieren (Balanced-Bracket-Slice), Pair-Pattern nur darin anwenden. Klassen-Regex: `^\s*(?:final\s+|abstract\s+)*class\s+(\w+)\s+extends\s+([\w\\]+)`. Smarty: einfache/doppelte Quotes + optionale Attribute zulassen.

## Akzeptanzkriterien

- [ ] Fixture-`metadata.php` mit `id/title/templates/settings/extend` erzeugt ausschließlich für die `extend`-Einträge Kanten.
- [ ] `abstract class X extends oxArticle` und `class Y extends \OxidEsales\...\Article` erzeugen Knoten + Kante.
- [ ] Bestehende Fälle (einfacher `class A extends B`) unverändert.

## DoD

- Fix + Fixture-Tests gemerged; Re-Index-Hinweis für PHP-Projekte.
