# NuGet-Audit-Waiver (P07)

Stand: 2026-11-20 · **Review-Termin: 2026-12-19**

## Warum

`CUE4Parse 1.2.2.202611` pinnt `Microsoft.Bcl.Memory 9.0.0` und
`SixLabors.ImageSharp 3.1.12`. Ein Upgrade würde den Parser brechen, deshalb
sind die bekannten Advisories vorübergehend per `NoWarn` (NU1902/NU1903)
ausgeblendet. **Das Audit selbst ist wieder aktiv** — neue Advisories anderer
Pakete erscheinen weiterhin.

## Konkrete, aktuell ausblendete Advisories

| Paket | Version | Schweregrad | Advisory |
|---|---|---|---|
| SixLabors.ImageSharp | 3.1.12 | Hoch | GHSA-jjfr-hcj7-qf5w |
| SixLabors.ImageSharp | 3.1.12 | Hoch | GHSA-v76p-62qx-wwq2 |
| SixLabors.ImageSharp | 3.1.12 | Hoch | GHSA-j3p4-wp97-rph4 |
| SixLabors.ImageSharp | 3.1.12 | Hoch | GHSA-j9gm-c75j-xc9q |
| SixLabors.ImageSharp | 3.1.12 | Mittel | GHSA-wmxv-xphr-5c9g |
| SixLabors.ImageSharp | 3.1.12 | Mittel | GHSA-gwg2-r3hj-4w44 |
| Microsoft.Bcl.Memory | 9.0.0 | Hoch | GHSA-73j8-2gch-69rq |

## Regeln

1. Kein blindes Verlängern des Waivers über den Review-Termin hinaus.
2. Bei einem CUE4Parse-Update: `NoWarn` entfernen, `dotnet restore` prüfen,
   nur noch tatsächlich nicht behebbare Advisories erneut aufnehmen — mit neuem Datum.
3. Die Advisories betreffen das Parsen von **Spielassets** (lokal, offline).
   Keine Netzwerkdienste, keine Benutzereingaben aus dem Internet.
