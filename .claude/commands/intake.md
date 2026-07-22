---
description: Rohwunsch des Stakeholders aufnehmen und via Requirements Engineer zu einem GitHub-Issue schärfen (Gate 1).
argument-hint: <freitext-anforderung oder bug-beschreibung>
---

Nimm die folgende Roh-Anforderung des Stakeholders auf:

$ARGUMENTS

Delegiere an den **requirements-engineer**-Agent. Er soll:
1. bei Unklarheit gezielt zurückfragen,
2. den Code read-only prüfen und Duplikate ausschließen,
3. **bei architektonisch relevanten Anforderungen** (neue Abhängigkeit, Querschnitt, Datenfluss, Persistenz, Schnittstelle, Performance-Pfad) den **solution-architect**-Agent konsultieren — dessen Machbarkeit/Constraints/Optionen fließen als „Architektur-Notizen"-Abschnitt ins Ticket, und das Ticket bekommt das Label `architecture`,
4. einen Ticket-Entwurf (INVEST + Akzeptanzkriterien + Scope-Grenze, ggf. + Architektur-Notizen) vorlegen.

**Gate 1 — Stakeholder-Bestätigung:** Zeige mir den Entwurf und lege das GitHub-Issue erst an, nachdem ich Formulierung und Scope bestätigt habe.
