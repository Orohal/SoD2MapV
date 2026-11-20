# Checkliste für Audit-Agenten (lesend, MAX. 30 Min)

Zweck: nachvollziehbare Audits ohne Permission Drift. Gilt für KiloCode, Iceman,
Copilot und jeden anderen Agenten. **Audits und Umsetzung strikt trennen.**

## 0. Harte Regeln (unverhandelbar)

- [ ] Nur **lesen**: `Get-Content`, `Select-String`, `Get-ChildItem`, `git status/log/diff/ls-files`, Python nur `-c` ohne Dateischreibzugriff.
- [ ] **Keine** Datei erstellen, ändern, löschen, umbenennen — auch keine temporären Analyse-Dateien.
- [ ] **Keine** Builds, Tests, Deployments, Paket-Installationen, Prozessstarts/-stops.
- [ ] **Keine** Commits, Pushes, Releases, keine Rechteerweiterung, kein `sudo`/`runas`.
- [ ] Findet der Agent etwas, das er „zum Prüfen" ausführen will → **stoppen und fragen**, nicht selbst entscheiden.

## 1. Vor dem Start

- [ ] Scope in einem Satz: welche Verzeichnisse/Dateien, welche Frage.
- [ ] Zeitbudget setzen: **25 Minuten**, danach Übergabe an frische Session.
- [ ] Regeln **am Anfang des Prompts** und **am Ende jedes Arbeitspakets** wiederholen — nicht nur einmal am Start.
- [ ] Rechte technisch begrenzen (Read-only-Sandbox / Tool-Allowlist), nicht nur im Prompt.

## 2. Während der Arbeit

- [ ] Immer `Datei:Zeile` als Beleg angeben; keine Aussage ohne Fundstelle.
- [ ] Trennen: **nachgewiesener Fehler** / **potenzielles Risiko** / **optionale Verbesserung**.
- [ ] Vermutungen ausdrücklich als solche markieren; keine erfundenen Funktionen.
- [ ] Alles lesen, was zusammenhängt (nicht isolierte Schnipsel bewerten).
- [ ] Bei `ConfigureAwait`/Threading/Rechten: erst den Aufrufer / die Kette prüfen, dann urteilen.
- [ ] Bestehendes, Funktionierendes positiv erwähnen — nicht nur Mängel listen.

## 3. Eskalation = Review-Fall

Sobald der Agent sagt: „ich brauche mehr Rechte", „ich muss das kurz ausführen",
„ich schreibe mir eine Hilfsdatei" → **Session stoppen**. Nicht gewähren.
Stattdessen: Findings des bisherigen Laufs übernehmen, neue Session mit engerem Scope starten.

## 4. Übergabe (Format)

1. Kurzfassung: Zustand, grösste Stärke, grösstes Risiko.
2. Priorisiert: Kritisch / Hoch / Mittel / Niedrig — je mit Fundstelle, Ursache, Auswirkung.
3. Verbesserungsvorschlag je Punkt: was / warum / wie / Nutzen / Aufwand / Risiko.
4. Roadmap in Phasen: Kritisches → Stabilität → Architektur → Performance → UX → Langfristig.
5. Bestätigung: **„Es wurde keine Datei verändert, kein Build ausgeführt, nichts committet."**

## 5. Nach dem Audit

- [ ] Findings mit einem **zweiten, unabhängigen Agenten** gegenlesen (Gegenprobe).
- [ ] Abweichungen klären — widersprüchliche Fundstellen erneut selbst lesen (siehe Newtonsoft-Fall).
- [ ] Umsetzung **nie im Audit-Agenten** machen, sondern in einer separaten Session mit expliziter Freigabe pro Commit.

## Prompt-Vorlage (kopierbar)

```text
Du führst einen AUSSCHLIESSLICH LESENDEN Audit durch. Zeitbudget: 25 Minuten.

Scope: <Verzeichnisse/Dateien> — Frage: <konkrete Frage>

HARTE REGELN: Keine Datei erstellen/ändern/löschen/umbenennen. Keine Builds,
keine Tests, keine Prozesse, keine Commits/Pushes, keine Paketinstallation,
keine Rechteerweiterung. Auch keine temporären Hilfsdateien. Wenn du etwas
ausführen oder schreiben willst: STOPP und fragen.

Arbeite mit Datei:Zeile-Belegen. Trenne nachgewiesene Fehler / Risiken /
optionale Verbesserungen. Markiere Vermutungen. Wiederhole die harten Regeln
am Ende jedes Arbeitspakets.

Liefere den Bericht direkt hier im Gespräch (keine Datei!) mit:
Kurzfassung, priorisierte Findings, Verbesserungsvorschläge, Roadmap und
der Bestätigung, dass nichts verändert wurde.

Nach 25 Minuten: laufende Findings zusammenfassen und beenden.
```
