# AGENTS.md

## Build & Deploy

```bash
cd D:\SPT41\Development\Killcam
dotnet build -c Debug
```

Post-build kopiert DLL + PDB automatisch nach `D:\SPT41\BepInEx\plugins\` (`SPTPath` in
`Shared.props` = zwei Ebenen über dem Repo).
Seit 0.9.15 übernimmt `tools/Deploy-Killcam.ps1` das Einspielen: alte Killcam-
und KillcamReplay-Dateien werden außerhalb von `plugins` gesichert; gesperrte
Dateien brechen das Deployment vor Änderungen ab. Die Config-Migration steht in
`src/ConfigMigration.cs` und läuft vor dem ersten Bind, ohne neue Configs zu überschreiben.
Offline bauen: `-p:CustomAfterMicrosoftCommonTargets=<absoluter Pfad>/tools/NoDeploy.targets`.

## Projekt

BepInEx 5.x / HarmonyX Client-Mod für SPT: Killcam-**Replay** aus Sicht des Killers
(aufgezeichneter Kamera-Pfad mit optionaler Live-Phase am Ende). Siehe README.md
für Installation, Einstellungen und Einschränkungen; Architektur und
Aufzeichnungsaufwand stehen in docs/IMPLEMENTATION.md.

- `src/Recording/SnapshotRecorder.cs` — Ringpuffer-Aufzeichnung (20 Hz, allokationsfrei im Hot Path)
- `src/Replay/ReplayController.cs` — Playback in LateUpdate, Kamera-Pose in onBeforeRender, IMGUI-Overlay + optionaler Debug-Hotkey (unbelegt, Advanced)
- `src/Plugin.Configuration.cs` — stabile Config-Schlüssel mit lesbaren F12-Tags; `DebugReplayHotkey` ersetzt seit 1.0.1 den alten `ReplayKey`, ohne dessen Belegung zu übernehmen
- `src/Patches/ReplayPatches.cs` — Raid-Start (vmethod_0), OnMakingShot, OnDead, DeferStop, PerfectCulling-Fix

## Wichtige Muster

Dieselben Harmony-Pitfalls wie in `D:\SPT41\Development\KillAndDamageInfo\CLAUDE.md`
(dekompilierte Quelle: `D:\SPT41\Development\_decompiled`). Insbesondere:

- **Deferred Stop**: `LocalGame.Stop()` per Prefix (return false) aufschieben, Re-Invoke
  mit `_isReentry`-Flag nach dem Replay. Sonst schwarzer Screen + State-Zerstörung.
- **NIE `PlayerCameraController.Destroy` aufrufen**: Das Destroy-Event setzt
  `EffectsController.player_0 = null`; wird der Spieler danach getroffen/gekillt, wirft
  `ActiveHealthController.Kill` → `EffectsController.method_9` eine NullRef und die
  Raid-Ende-Sequenz bricht ab (Soft-Lock, Stop wird nie gerufen). Stattdessen: Kamera-Pose
  in `Application.onBeforeRender` stompen — läuft nach allen `LateUpdate`s und gewinnt das
  gerenderte Bild gegen den weiterlaufenden Controller (inkl. dessen `ForceSetPosition`).
- **Todes-Fade unterdrücken**: `EffectsController.method_9` (DeathFade + FastBlur) feuert
  beim Kill NACH unserem `OnDead`-Postfix und legt sonst Schwarz über das Replay. Prefix
  return false solange `PendingReplayKiller != null` (empirisch via Log verifiziert).
- **Pitch-Mapping bestätigt**: `Quaternion.Euler(rotation.y, rotation.x, 0)` aus
  `Player.Rotation` (x=Yaw, y=Pitch) stimmt mit der tatsächlichen Blickrichtung überein —
  in-game getestet, kein Invert nötig.
- **Puppet ausschließlich aus visuellen Komponenten aufbauen**: `PuppetVisualHierarchy`
  erzeugt eigene Transform-, MeshRenderer-/MeshFilter-, SkinnedMeshRenderer- und
  LODGroup-Objekte. `Object.Instantiate` des nativen Körpers ist auch unter einem
  inaktiven Parent ungeeignet: Serialisierung kann bereits dabei laufen. Keine
  Spielskripte kopieren und anschließend entfernen, keinen globalen Unity-Logger
  stummschalten. Skin-Knochen, rootBone, interne Probe-Anker und LOD-Renderer müssen
  auf die eigene Hierarchie zeigen; fremde Skin-Knochen lösen den sichtbaren
  Silhouette-Fallback aus. Shared Meshes/Materials gehören weiter EFT und werden
  nie zerstört. Teilaufbau und teilweise versteckte Originale müssen bei Fehlern
  aufgeräumt werden. Siehe `docs/SCRIPT-FREE-PUPPET-20261019.md` und
  `tools/PuppetVisualChecks/Verify.ps1`; Offline-Prüfung ersetzt keinen GPU-Replay.
- **Waffe in der Hand ≠ SlotView**: `PlayerBody.SlotViews` deckt getragene Ausrüstung ab,
  aber die Waffe in der Hand ist ein eigenes View-Objekt unter `Player.WeaponRoot.Original`
  (und empirisch kein Nachfahre von `player.gameObject`). Beim Verstecken von Spielern beides
  sweepen — inkl. `Light`-Komponenten (Taschenlampe sitzt an der Hand-Waffe).
- Postfixes in Gameplay-Pfaden (OnDead, OnMakingShot) immer in try/catch.
- **RespawnApi-Interop** (seit 0.9.8): `src/Interop/RedeployRespawnInterop.cs` ist eine Kopie
  aus dem Redeploy-Repo (`interop/`), nur der Namespace ist geändert. Nie von Hand ändern,
  bei neuen Interop-Versionen neu kopieren. `LocalBodyTracker` ist `internal`; Felder dieses
  Typs müssen `internal` sein (CS0052). `BodyChanged` kann mitten in Cores Ersetzungs-Pipeline
  laufen: dort nur `StopPlayback` (fasst weder `PlayerCameraController` noch Bodies an), nie
  einen Respawn starten, den Raid stoppen oder einen Body zerstören. Im `OnDead`-Postfix gilt
  `ReferenceEquals(MainPlayer, __instance)` statt nur `IsYourPlayer`.
- **Respawn-Hold** (seit 0.9.9, `src/Patches/HoldRespawnPatch.cs`): Postfix auf
  `Redeploy.Core.Engine.RespawnEngine.RunRespawn` (per Name, kein Compile-Bezug) ersetzt die
  zurückgegebene Coroutine durch „warten, dann Original schrittweise". Nur für den Body des
  laufenden Todes-Replays, nie in ArenaMode-Raids, gedeckelt wie der Deferred-Stop. Die
  Zustandslogik steht rein in `src/Replay/RespawnHandoff.cs` (keine Unity-Typen) und wird
  offline von `tools/HandoffChecks` geprüft (`dotnet run --project tools/HandoffChecks -c Release`,
  Token `HANDOFF_CHECKS_PASSED`, ohne Core-Build `HANDOFF_CHECKS_PARTIAL` mit Exit 3; baut nichts
  ins Spiel). Ablauf: Live-Tail → FinalFade → HoldBlack → Freigabe → Pipeline läuft synchron →
  `BodyChanged` beendet das Replay → Fade-in. Gibt der Hold selbst auf (Raid endet, Deckel),
  ruft er `AbandonWaiting`, damit das Replay nicht für einen Respawn schwarz wird, der nicht kommt.
  Neue Zustandsentscheidungen gehören in `RespawnHandoff` (rein, testbar), nicht in den
  MonoBehaviour. Skip-Taste nicht über `KeyboardShortcut.IsDown()` prüfen: das feuert nicht,
  solange irgendeine andere Taste gehalten wird.
  Unity disposed gestoppte Coroutinen nicht: keine `finally`-Aufräumlogik um `yield`s, stattdessen
  Deckel und Generationszähler in `RunDeathReplay`.
- **Tools/Artifacts-Ordner**: `Killcam.csproj` schließt `tools\**` und `artifacts\**`
  aus; sonst kompiliert der SDK-Glob Prüfprogramme oder fehlerhafte Mutationstests
  ins Plugin. `pwsh -File tools/PuppetVisualChecks/Verify.ps1` prüft den echten
  Produktionscode gegen Verhaltens-Stand-ins, erkennt gezielte Defekte und baut
  danach das Plugin mit dem expliziten NoDeploy-Override.
