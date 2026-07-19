# KillcamReplay (Prototyp)

Echte Killcam für SPT: Beim Tod wird die letzte Aktion **aus der Sicht des Killers** abgespielt —
kein Live-Blick auf den Killer (wie in KillAndDamageInfo), sondern ein Replay der aufgezeichneten
Blickrichtung und Position der letzten Sekunden vor dem Kill.

## Funktionsweise

**Aufzeichnung (während des Raids):**
Ein `SnapshotRecorder` sampelt mit fester Rate (Standard 20 Hz) für jeden lebenden Spieler/Bot
Kameraposition (`Player.CameraPosition`), Blickrichtung (`Player.Rotation`, Yaw/Pitch) und
Körperhaltung (`PoseLevel`, `IsInPronePose`) in einen Ringpuffer (Standard 15 s). Schüsse
werden über einen Postfix auf `Player.OnMakingShot` mit Zeitstempel markiert.

**Replay (beim Tod):**
`Player.OnDead` liefert den Killer (`LastAggressor`). Aus dessen Ringpuffer wird das Fenster
der letzten N Sekunden (Standard 5 s) extrahiert. `LocalGame.Stop()` wird deferred (gleiches
Muster wie die KillAndDamageInfo-Killcam) und die Hauptkamera entlang des aufgezeichneten
Pfads interpoliert (Position: Lerp, Yaw/Pitch: LerpAngle). Die Pose wird in
`Application.onBeforeRender` gesetzt — nach allen `LateUpdate`s, sodass der weiterlaufende
`PlayerCameraController` (Corpse-Cam) das gerenderte Bild nicht überschreiben kann. Es wird
bewusst **kein** Controller zerstört oder erzeugt: `PlayerCameraController.Destroy` hängt den
`EffectsController` vom Spieler ab, dessen Death-Handler dann NullRefs wirft und die
Raid-Ende-Sequenz abbricht (Soft-Lock). Der Todes-Fade (`EffectsController.method_9`,
DeathFade + FastBlur) wird während des Replays per Prefix unterdrückt, sonst läge er schwarz
über dem Replay.

**Victim-Ghost (Phase 2):**
Während des Replays läuft eine halbtransparente Silhouette (`ReplayGhost`: Kapsel-Körper mit
haltungsabhängiger Höhe, Kopf-Kugel auf Augenhöhe, Blickrichtungs-Balken) den aufgezeichneten
Pfad des Opfers ab — man sieht sich selbst durch die Augen des Killers. Aus collider-losen
Primitives gebaut, berührt keine Spielsysteme. Beim F9-Debug-Replay ist der Ghost der eigene
Pfad: vor einen Bot stellen, F9 drücken, sich selbst zusehen.

## Performance-Budget

- **Idle-Kosten pro Frame:** 2 float-Vergleiche (Sample nicht fällig → early-out).
- **Pro Sample-Tick (20×/s):** pro lebendem Spieler 1 Transform-Read + 1 Vector2-Read +
  1 Struct-Write in ein vorallokiertes Array. Bei ~30 Bots: Mikrosekunden-Bereich.
- **Keine GC-Allokationen im Hot Path:** Ringpuffer sind vorallokiert (Structs), das
  Track-Dictionary wächst nur, wenn ein neuer Spieler spawnt. Extraktion des Replay-Fensters
  allokiert nur einmal beim Tod.
- **Speicher:** 32 Bytes/Snapshot × 20 Hz × 15 s ≈ 9,6 KB pro Spieler; bei 40 Spielern ≈ 385 KB.

## Konfiguration (BepInEx F12 / Config-Datei)

| Option | Default | Beschreibung |
|---|---|---|
| Recording.SampleRate | 20 Hz | Aufnahmerate |
| Recording.BufferSeconds | 15 s | Ringpuffer-Länge |
| Replay.Duration | 5 s | Replay-Länge |
| Replay.DeathReplay | true | Replay beim eigenen Tod |
| Replay.ShowVictimGhost | true | Eigene Silhouette (Ghost) im Replay anzeigen |
| Replay.HideLiveBots | true | Lebende Bots während des Replays ausblenden |
| Replay.InvertPitch | false | Vertikale Blickrichtung invertieren (falls Kamera falsch kippt) |
| Debug.ReplayKey | F9 | Replay des nächstgelegenen Bots sofort abspielen (Test ohne Sterben) |

## Bekannte Einschränkungen (Prototyp)

- **Die Welt läuft live weiter** — lebende Bots (inkl. des Killers) werden während des Replays
  aber ausgeblendet (`LivePlayerHider`: nur `Renderer.enabled`, KI/Logik laufen weiter; danach
  Wiederherstellung). Nur das Opfer wird als Ghost auf dem aufgezeichneten Pfad dargestellt;
  der eigene Körper liegt zusätzlich an der Todesstelle. Weitere Akteure als Ghosts (z.B. der
  Killer bei Drittperspektive) wären ein späterer Ausbau.
- **Kein Waffenmodell/Mündungsfeuer im Replay** — Schüsse werden nur als Overlay-Marker
  („● SCHUSS") angezeigt.
- **Das „Killed in Action"-Panel** erscheint als UI-Overlay über dem Replay (wie bei der
  KillAndDamageInfo-Killcam).
- **Koexistenz mit KillAndDamageInfo:** Dessen Live-Killcam besitzt ebenfalls die Todessequenz
  (deferred Stop + Kamera-Übernahme). Ist die Mod geladen, deaktiviert sich das Death-Replay
  automatisch (Log-Warnung); das F9-Debug-Replay funktioniert weiterhin. Zum Testen des
  Death-Replays die KillAndDamageInfo.dll temporär aus `BepInEx/plugins` nehmen.

## Build

```
dotnet build -c Debug
```

Kopiert DLL + PDB automatisch nach `C:\SPT\BepInEx\plugins\`.
