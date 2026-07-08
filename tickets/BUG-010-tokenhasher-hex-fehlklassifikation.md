# BUG-010 — 64-Hex-Token wird als „bereits gehasht" fehlklassifiziert: Klartext-Speicherung + permanenter Auth-Lockout

**Schweregrad:** Hoch · **Status:** Bestätigt · **Bereich:** Auth · **Security**

## Kontext

`TokenHasher.LooksHashed` klassifiziert jeden 64-Zeichen-Hex-String als Digest; `EnsureHashed` reicht ihn dann unverändert durch ([TokenHasher.cs:20-25](../src/Shonkor.Infrastructure/Services/TokenHasher.cs)). `ProjectManager.AddProject` akzeptiert einen **caller-gelieferten** API-Key ([ProjectManager.cs:195-201](../src/Shonkor.Infrastructure/Services/ProjectManager.cs)): Ein Token, das zufällig genau 64 Hex-Zeichen lang ist (die häufigste Form eines 32-Byte-Tokens), wird im **Klartext** in `projects.json` gespeichert — und weil `Verify` das präsentierte Token hasht (`SHA256(token) != token`), schlägt die Authentifizierung für diesen Key **immer** fehl, ohne erklärenden Fehler.

Nebenbefund (gleiches Modul): `LooksHashed` akzeptiert Großbuchstaben-Hex, `Hash` emittiert Kleinbuchstaben, `Verify` vergleicht byte-genau → ein groß geschriebener gespeicherter Digest matcht nie.

## Reproduktion

Projekt/User mit API-Key `"a" * 64` (bzw. 64 zufälligen Hex-Zeichen) anlegen → Key steht im Klartext in `projects.json`; Auth mit genau diesem Key liefert 401.

## Fix

Shape-Sniffing abschaffen: Hashes selbstbeschreibend speichern (`sha256:<hex>`); `EnsureHashed` hasht alles ohne Präfix und stempelt das Präfix; Migration bestehender Einträge einmalig beim Laden. `Verify` normalisiert (`ToLowerInvariant`) vor dem Vergleich.

## Akzeptanzkriterien

- [ ] Ein 64-Hex-Klartext-Token wird gehasht gespeichert und authentifiziert korrekt.
- [ ] Bestehende gehashte Einträge (mit/ohne Präfix, groß/klein) verifizieren weiterhin.
- [ ] `projects.json` enthält nach Migration keine Klartext-Secrets (Test über das Datei-Format).

## DoD

- Fix + Migration + Tests gemerged; Format-Änderung im CHANGELOG.
