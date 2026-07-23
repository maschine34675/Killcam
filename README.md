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

**Weiches Ende (Live-Tail):**
Am Ende des aufgezeichneten Pfads (dem Kill-Moment) endet das Replay nicht hart: kurzer
Fade zu Schwarz (~0,2s), unter Voll-Schwarz der Szenenwechsel auf die Live-Welt (Puppet
weg, versteckte Bots und die eigene Leiche wieder sichtbar — nur der Killer bleibt
ausgeblendet), Fade zurück, dann folgt die Kamera für `LiveTailSeconds` (Standard 2s)
**live den Augen des Killers** (`FollowKillerInTail`; Fallback: letzte Replay-Position,
falls er inzwischen tot ist). Danach blendet das Replay selbst zu Schwarz ab und hält
Schwarz, bis der re-invokte `LocalGame.Stop` seinen eigenen Blackscreen aufgezogen hat
(erkannt über `DeferStopPatch.StopReinvokedAt` + 0,8s; Not-Timeout 4s) — die Corpse-Cam
blitzt also nie mehr zwischen Replay-Ende und Raid-Ende-Übergang auf. Wurde `Stop` nicht
deferred (z.B. weil eine Respawn-Mod den Tod abgefangen hat), wird die Kamera nach dem
Tail direkt zurückgegeben — kein Schwarz-Halten, das sonst den Respawn-Prompt verdecken
würde. Beim F9-Debug-Replay ist der Tail bewusst deaktiviert (der Spieler lebt und wäre
so lange blind).

**Victim-Ghost (Phase 2/3):**
Während des Replays sieht man sich selbst durch die Augen des Killers. Zwei Darstellungen:

- **Echtes Charaktermodell** (`GhostPuppet`, Standard): Der `BoneRecorder` zeichnet das
  komplette Skelett des lokalen Spielers auf (nur der lokale Spieler — deshalb bezahlbar:
  ~einige hundert Transform-Reads pro Tick, flache vorallokierte Arrays, wenige MB). Beim
  Replay wird die Leiche geklont — `Corpse.CreateCorpse` konvertiert das echte, angezogene
  Spieler-GameObject in-place, der Klon trägt also Kleidung + Gear. Vor dem Strippen wird auf
  jedem Renderer des Klons `shadowCastingMode = On` erzwungen — sonst bleiben Kopf/Oberkörper
  unsichtbar, weil EFT sie beim eigenen Spieler in Egoperspektive per
  `ShadowCastingMode.ShadowsOnly` rendert (die Corpse-Konvertierung setzt das nie zurück) und
  ein roher Klon diesen Zustand mit-kopiert. Der naheliegende Weg über
  `PlayerBody.UpdatePlayerRenders(ThirdPerson, ...)` — Sonys eigene Methode dafür — funktioniert
  auf dem Klon NICHT: sie iteriert `PlayerBody.BodySkins`, das erst zur Laufzeit (Awake/Equip)
  befüllt wird und auf einem Klon, dessen Awake wir bewusst nie laufen lassen, leer ist (stiller
  No-Op, kein Fehler). Der direkte Renderer-Sweep umgeht das komplett. Der Klon wird unter
  einem INAKTIVEN Holder
  instanziert (kein Awake läuft), alle Scripts/Physik/Animator per `DestroyImmediate`
  gestrippt (kein Awake → keine OnDestroy-Seiteneffekte), Knochen per Hierarchie-Pfad gemappt
  und pro Frame mit den aufgezeichneten Posen überschrieben — **kein Animator nötig**, exakte
  Wiedergabe von Gang, Lean und Zielen. Die Quelle (Leiche bzw. Live-Körper) wird nach dem
  Klonen ausgeblendet, damit sie nicht neben dem laufenden Ghost sichtbar bleibt.
- **Silhouette** (`ReplayGhost`, automatischer Fallback): Kapsel-Körper mit haltungsabhängiger
  Höhe, Kopf-Kugel, Blickrichtungs-Balken — falls Puppet-Erstellung fehlschlägt oder
  `UseRealModelGhost` aus ist.

Beim F9-Debug-Replay ist der Ghost der eigene Pfad (Puppet-Quelle = lebender Körper; der
eigene Kopf bleibt dort FPS-bedingt unsichtbar): vor einen Bot stellen, F9, sich selbst zusehen.

## Performance-Budget

- **Idle-Kosten pro Frame:** 2 float-Vergleiche (Sample nicht fällig → early-out).
- **Pro Sample-Tick (20×/s):** pro lebendem Spieler 1 Transform-Read + 1 Vector2-Read +
  1 Struct-Write in ein vorallokiertes Array. Bei ~30 Bots: Mikrosekunden-Bereich.
- **Keine GC-Allokationen im Hot Path:** Ringpuffer sind vorallokiert (Structs), das
  Track-Dictionary wächst nur, wenn ein neuer Spieler spawnt. Extraktion des Replay-Fensters
  allokiert nur einmal beim Tod.
- **Speicher:** 32 Bytes/Snapshot × 20 Hz × 15 s ≈ 9,6 KB pro Spieler; bei 40 Spielern ≈ 385 KB.
  Der Bone-Recorder (nur lokaler Spieler) kommt je nach Skelettgröße auf einige MB
  (wird beim Raid-Start geloggt).

## Konfiguration (BepInEx F12 / Config-Datei)

| Option | Default | Beschreibung |
|---|---|---|
| Recording.SampleRate | 20 Hz | Aufnahmerate |
| Recording.BufferSeconds | 15 s | Ringpuffer-Länge |
| Replay.Duration | 5 s | Replay-Länge |
| Replay.LiveTailSeconds | 2 s | Nach Replay-Ende noch live weiterzeigen (0 = aus) |
| Replay.FollowKillerInTail | true | Im Live-Nachlauf dem Killer live folgen statt statisch zu verharren |
| Replay.DeathReplay | true | Replay beim eigenen Tod |
| Replay.ShowVictimGhost | true | Eigene Silhouette (Ghost) im Replay anzeigen |
| Replay.UseRealModelGhost | true | Echtes Charaktermodell statt Silhouette (Fallback automatisch) |
| Replay.HideLiveBots | true | Lebende Bots während des Replays ausblenden |
| Replay.InvertPitch | false | Vertikale Blickrichtung invertieren (falls Kamera falsch kippt) |
| Debug.ReplayKey | F9 | Replay des nächstgelegenen Bots sofort abspielen (Test ohne Sterben) |

## Bekannte Einschränkungen (Prototyp)

- **Die Welt läuft live weiter** — lebende Bots (inkl. des Killers) werden während des Replays
  aber ausgeblendet (`LivePlayerHider`: `Renderer.enabled`, KI/Logik laufen weiter; danach
  Wiederherstellung). Ausrüstungs-Ansichten (Waffe, Chest Rig, Rucksack, Kopfbedeckung, ...)
  sind gepoolte Objekte (`AssetPoolObject`), die an einem Bone hängen — nicht garantiert
  Nachfahren von `player.gameObject` — deshalb wird zusätzlich `PlayerBody.SlotViews`
  durchlaufen (die eigene, autoritative Liste des Spiels für jede aktuell ausgerüstete
  Ansicht) und deren `Renderers` explizit versteckt. Die Waffe **in der Hand** ist dabei
  KEIN SlotView-Modell (der Slot unterdrückt sein Körper-Modell, solange das Item in der
  Hand ist) — sie ist ein eigenes View-Objekt des Hands-Controllers unter dem
  `WeaponRoot`-Bone und wird separat gesweept. Eine Taschenlampe ist außerdem kein
  Renderer, sondern ein `UnityEngine.Light`-Component (sitzt typischerweise an der
  Hand-Waffe), das beim reinen Verstecken der Mesh weiterleuchtet — deshalb werden
  zusätzlich alle `Light`-Komponenten (Spieler-Hierarchie, Slots, WeaponRoot) deaktiviert.
  Die eigene Leiche/der Live-Körper
  (Puppet-Quelle) wird ebenfalls ausgeblendet, sobald der Klon steht. Weitere Akteure als
  Ghosts (z.B. der Killer bei Drittperspektive) wären ein späterer Ausbau.
- **Kein Waffenmodell/Mündungsfeuer im Replay** — Schüsse werden nur als Overlay-Marker
  („● SCHUSS") angezeigt.
- **Das „Killed in Action"-Panel** erscheint als UI-Overlay über dem Replay (wie bei der
  KillAndDamageInfo-Killcam).
- **Waffe am Puppet nicht sichtbar**: Beim Klonen der Leiche wirft die Odin/Sirenix-
  Deserialisierung des Waffen-Items (`Weapon.OnAfterDeserialize`) eine NullReferenceException
  — das Item erwartet Backend-/Template-Kontext, den ein roher `Object.Instantiate` außerhalb
  der normalen Item-Factory nicht mitbringt. Passiert intern in Unitys Deserialisierung, wird
  nicht an uns durchgereicht (kein Crash), das Waffen-Mesh bleibt aber unsichtbar. Ein
  belastbarer Fix (z.B. nur das Waffen-Mesh ohne Item-Logik separat an die gemappte Hand-Bone
  hängen) ist ein eigenes, tieferes Stück Arbeit — aktuell nicht umgesetzt.
- **Koexistenz mit KillAndDamageInfo:** Dessen Live-Killcam besitzt ebenfalls die Todessequenz
  (deferred Stop + Kamera-Übernahme). Ist die Mod geladen, deaktiviert sich das Death-Replay
  automatisch (Log-Warnung); das F9-Debug-Replay funktioniert weiterhin. Zum Testen des
  Death-Replays die KillAndDamageInfo.dll temporär aus `BepInEx/plugins` nehmen.
- **Respawn-Kompatibilität (z.B. CorpseRun):** Die Aufzeichnung wird beim eigenen Tod
  absichtlich NICHT dauerhaft gestoppt (nur während der ~10s, die ein Replay aktiv den
  Bildschirm belegt) — sonst bricht die Aufzeichnung nach dem ersten Tod endgültig ab und
  jedes weitere Replay in derselben Runde scheitert mit „Killer track too short". Der
  `BoneRecorder` erkennt einen Charakterwechsel (neues Player-GameObject nach Respawn) und
  initialisiert sich automatisch neu. In-game verifiziert mit CorpseRun (mehrere Tode/Replays
  in einer Runde).

## Build

```
dotnet build -c Debug
```

Kopiert DLL + PDB automatisch nach `C:\SPT\BepInEx\plugins\`.
