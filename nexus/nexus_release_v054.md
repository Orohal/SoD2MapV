# SoD2MapV v0.5.4

## Highlights
- **Item-Injektion (GUI/CLI):** Neuer Dialog „Inject item…“ (im Menü „Inject ▾“) legt Item-Stacks in den Rucksack eines Überlebenden. Presets (Benzinkanister, Auto-Reparaturkasten) plus **automatisch aus dem gewählten Save abgeleitete** Item-Presets (aktualisierbar), manuelle Eingabe, Backup und Self-Check.
- **Waffen-Injektion gefixt:** Klassenbezogene Vorlagenwahl und `StackCount` optional – Ranged/Melee-Waffen (z.B. `Assault_FAL_C`) lassen sich korrekt injizieren.
- **Savegame-Parsing robuster:** `ByteProperty`-Enum-Namen, sized Arrays, `MapProperty` sowie Maskierung fehlerhafter High-Dwords in Größenfeldern.
- **CLI:** `--saveitems`, `--saveitemlist`.
- **README:** Virenwarnung/False-Positive einklappbar.

## Notes
- Items können nur injiziert werden, wenn der Item-Typ/Klasse in der `ItemLibrary` des Saves bereits eine nutzbare Instanz-Vorlage hat; freie Rucksack-Slots erforderlich. Injizierte Waffen übernehmen den Zustand der geklonten Vorlagen-Instanz (Haltbarkeit/Mods/Ammo-Referenzen).
- Vor dem Schreiben: Spiel im Menü/beendet halten; automatische `.bak`-Backups werden angelegt. Unsignierte Builds können False-Positive-Warnungen auslösen (siehe README/Hashes).