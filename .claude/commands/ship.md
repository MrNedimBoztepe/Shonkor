---
description: Ein Issue umsetzen — Developer implementiert, Tester verifiziert unabhängig, PR gegen develop; Merge bleibt Stakeholder-Gate (Gate 3).
argument-hint: <issue-nummer>
---

Bringe Issue #$ARGUMENTS durch die Umsetzungs-Pipeline. Führe die Schritte **sequentiell** aus (Gewaltenteilung, WIP=1):

1. **Ready-Check**: Stelle sicher, dass das Issue testbare Akzeptanzkriterien hat. Fehlt das, brich ab und schick es zurück in /intake.
2. **Signifikanz-Check** (proportional/gated): Ist das Ticket **architektonisch signifikant**? Ja, wenn es das Label `architecture` trägt oder einen „Architektur-Notizen"-Abschnitt hat, oder wenn es Struktur/Abhängigkeit/Datenfluss/Persistenz/Schnittstelle/Performance-Pfad berührt. Triviale, lokale Fixes (wie ein Einzeiler) sind NICHT signifikant — dann überspringe die Architekt-Schritte 4 und 6a.
3. **Ticket-Start-Ritual** (VOR dem Code): `gh issue edit $ARGUMENTS --add-label "in progress"` **und** Projects-Board-Status auf „In Progress" setzen. Board `Shonkor` (Nr. 1, owner `MrNedimBoztepe`): item-id via `gh project item-list 1 --owner MrNedimBoztepe --limit 500 --format json --jq '.items[]|select(.content.number==$ARGUMENTS)|.id'` (das Board hat >230 Items — ein zu kleines `--limit` findet neuere Issues durch Paginierung nicht), dann `gh project item-edit --id <itemId> --project-id PVT_kwHOEUSPt84Bc7M- --field-id PVTSSF_lAHOEUSPt84Bc7M-zhXgeHM --single-select-option-id 47fc9ee4` (In Progress). Ist das Issue nicht auf dem Board, füge es hinzu. Label und Board-Status dürfen **nie** widersprechen.
4. **Architekt-Design-Beratung** (nur wenn signifikant): Delegiere an den **solution-architect**-Agent — er holt sich den aktuellen Graph-/Tech-Stand und liefert dem Developer einen knappen, umsetzbaren Lösungsweg (wo der Code hingehört, welche kanonischen Bausteine, welche Invarianten/Blast-Radius-Fallen). Gib diese Design-Notiz an den Developer weiter.
5. **Developer**: Delegiere an den **developer**-Agent — Branch verlinkt via `gh issue develop $ARGUMENTS --base develop --name typ/$ARGUMENTS-slug`, implementieren (ggf. entlang der Architekt-Design-Notiz), lokal bauen/testen, PR gegen `develop` mit `Closes #$ARGUMENTS`, Closing-Link verifizieren.
6. **Unabhängige Prüfung** (zwei getrennte Linsen):
   - **6a. Architektur-Review** (nur wenn signifikant): Delegiere an den **solution-architect** — reviewt den PR auf Design/Muster/Kanonik/Kopplung/Tech-Aktualität. Urteil „architektonisch OK" oder „Nacharbeit" mit belegter Begründung. **Advisory** (siehe Gate 3), kein harter Block.
   - **6b. Tester**: Delegiere an den **tester**-Agent — unabhängige, adversariale Verifikation gegen die Akzeptanzkriterien, Tests laufen lassen, Urteil GRÜN/ROT mit Belegen.
7. Bei **Tester-ROT**: harter Loop-back — zurück an den Developer mit dem konkreten Befund; wiederhole ab Schritt 5, bis grün.

**Gate 3 — Stakeholder-Merge:** Wenn der Tester GRÜN gibt, fasse zusammen: PR, Testnachweis + Tester-Freigabe **und** (falls signifikant) das Architektur-Review-Urteil. Meldet der Architekt „Nacharbeit", ist das **beratend**: lege es mir vor, ICH entscheide, ob zurück an den Developer oder bewusst als akzeptierte Tech-Schuld gemergt (dann Follow-up-Ticket). Frage mich, ob gemergt werden soll. Merge `develop`→`main` nur auf meine ausdrückliche Freigabe.

**Abschluss-Ritus (nach meinem Merge-OK):** mergen → `git checkout develop && git pull` → Issue schließen falls nicht auto-geschlossen (`gh issue view $ARGUMENTS --json state`; PRs zielen auf `develop`, Auto-Close ist nicht garantiert) → `in progress`-Label entfernen → Board-Status auf „Done" (`--single-select-option-id 98236657`) → gemergten Branch löschen → für deferte/offene Kriterien Follow-up-Issues anlegen. Label und Board-Status müssen am Ende übereinstimmen und beide „erledigt" zeigen.
