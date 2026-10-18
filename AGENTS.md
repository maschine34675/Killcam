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
- `src/Replay/ReplayController.cs` — Playback in LateUpdate, Kamera-Pose in onBeforeRender, IMGUI-Overlay + F9-Debug-Replay
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
- **`DestroyImmediate` wirft bei blockierter `[RequireComponent]`-Abhängigkeit KEINE
  Exception**: Unity loggt nur intern „Can't remove X because Y depends on it" und no-opt
  still — try/catch kann das weder erkennen noch verhindern. Blockierte Versuche gar nicht
  erst unternehmen: vor jedem Löschversuch per Reflection prüfen, ob ein Geschwister-Component
  den Typ per `[RequireComponent]` benötigt (Basisklassen manuell walken — Unity erzwingt
  auch geerbte Anforderungen wie `CharacterJoint`→`Joint`→`Rigidbody`, das Attribut ist aber
  nicht als vererbt markiert). Siehe `GhostPuppet.IsRequiredBySibling`.
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
- **Tools-Ordner**: `Killcam.csproj` schließt `tools\**` aus; sonst kompiliert der SDK-Glob
  die Prüfprogramme ins Plugin.
