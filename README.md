# KillcamReplay (Prototyp)

Echte Killcam für SPT: Beim Tod wird die letzte Aktion **aus der Sicht des Killers** abgespielt —
kein Live-Blick auf den Killer (wie in KillAndDamageInfo), sondern ein Replay der aufgezeichneten
Blickrichtung und Position der letzten Sekunden vor dem Kill.

## Funktionsweise

**Aufzeichnung (während des Raids):**
Ein `SnapshotRecorder` sampelt mit fester Rate (Standard 20 Hz) für jeden lebenden Spieler/Bot
Kameraposition (`Player.CameraPosition`) und Blickrichtung (`Player.Rotation`, Yaw/Pitch) in
einen Ringpuffer (Standard 15 s). Schüsse werden über einen Postfix auf `Player.OnMakingShot`
mit Zeitstempel markiert.

**Replay (beim Tod):**
`Player.OnDead` liefert den Killer (`LastAggressor`). Aus dessen Ringpuffer wird das Fenster
der letzten N Sekunden (Standard 5 s) extrahiert. `LocalGame.Stop()` wird deferred (gleiches
Muster wie die KillAndDamageInfo-Killcam), der `PlayerCameraController` des toten Spielers
entfernt und die Hauptkamera in `LateUpdate` entlang des aufgezeichneten Pfads interpoliert
(Position: Lerp, Yaw/Pitch: LerpAngle). Danach wird die Kamera zurückgegeben und Stop()
nachgeholt.

## Performance-Budget

- **Idle-Kosten pro Frame:** 2 float-Vergleiche (Sample nicht fällig → early-out).
- **Pro Sample-Tick (20×/s):** pro lebendem Spieler 1 Transform-Read + 1 Vector2-Read +
  1 Struct-Write in ein vorallokiertes Array. Bei ~30 Bots: Mikrosekunden-Bereich.
- **Keine GC-Allokationen im Hot Path:** Ringpuffer sind vorallokiert (Structs), das
  Track-Dictionary wächst nur, wenn ein neuer Spieler spawnt. Extraktion des Replay-Fensters
  allokiert nur einmal beim Tod.
- **Speicher:** 24 Bytes/Snapshot × 20 Hz × 15 s ≈ 7,2 KB pro Spieler; bei 40 Spielern ≈ 290 KB.

## Konfiguration (BepInEx F12 / Config-Datei)

| Option | Default | Beschreibung |
|---|---|---|
| Recording.SampleRate | 20 Hz | Aufnahmerate |
| Recording.BufferSeconds | 15 s | Ringpuffer-Länge |
| Replay.Duration | 5 s | Replay-Länge |
| Replay.DeathReplay | true | Replay beim eigenen Tod |
| Replay.InvertPitch | false | Vertikale Blickrichtung invertieren (falls Kamera falsch kippt) |
| Debug.ReplayKey | F9 | Replay des nächstgelegenen Bots sofort abspielen (Test ohne Sterben) |

## Bekannte Einschränkungen (Prototyp)

- **Nur der Kamera-Pfad wird abgespielt** — die Welt läuft live weiter. Bots (inkl. des Killers
  selbst) stehen/laufen an ihrer aktuellen Position, nicht an der aufgezeichneten. Der eigene
  Körper liegt bereits an der Todesstelle. Ein vollwertiges Replay (Bots/Opfer entlang der
  aufgezeichneten Pfade als Ghosts bewegen) wäre Phase 2.
- **Kein Waffenmodell/Mündungsfeuer im Replay** — Schüsse werden nur als Overlay-Marker
  („● SCHUSS") angezeigt.
- **Pitch-Vorzeichen ungetestet** — falls die Kamera vertikal falsch kippt: `Replay.InvertPitch`
  aktivieren.
- **Koexistenz mit KillAndDamageInfo:** Dessen Live-Killcam besitzt ebenfalls die Todessequenz
  (deferred Stop + Kamera-Übernahme). Ist die Mod geladen, deaktiviert sich das Death-Replay
  automatisch (Log-Warnung); das F9-Debug-Replay funktioniert weiterhin. Zum Testen des
  Death-Replays die KillAndDamageInfo.dll temporär aus `BepInEx/plugins` nehmen.

## Build

```
dotnet build -c Debug
```

Kopiert DLL + PDB automatisch nach `C:\SPT\BepInEx\plugins\`.
