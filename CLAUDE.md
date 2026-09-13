# CLAUDE.md

## Build & Deploy

```bash
cd C:\SPT\Development\KillcamReplay
dotnet build -c Debug
```

Post-build kopiert DLL + PDB automatisch nach `C:\SPT\BepInEx\plugins\`.

## Projekt

BepInEx 5.x / HarmonyX Client-Mod für SPT: Killcam-**Replay** aus Sicht des Killers
(aufgezeichneter Kamera-Pfad, kein Live-Blick). Prototyp — siehe README.md für
Architektur, Performance-Budget und bekannte Einschränkungen.

- `src/Recording/SnapshotRecorder.cs` — Ringpuffer-Aufzeichnung (20 Hz, allokationsfrei im Hot Path)
- `src/Replay/ReplayController.cs` — Kamera-Pfad-Playback in LateUpdate + IMGUI-Overlay + F9-Debug-Replay
- `src/Patches/ReplayPatches.cs` — Raid-Start (vmethod_0), OnMakingShot, OnDead, DeferStop, PerfectCulling-Fix

## Wichtige Muster

Dieselben Harmony-Pitfalls wie in `C:\SPT\Development\KillAndDamageInfo\CLAUDE.md`
(dekompilierte Quelle: `C:\SPT\Development\decompiled\Assembly-CSharp`). Insbesondere:

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
- **Koexistenz**: Death-Replay deaktiviert sich automatisch, wenn
  `com.local.killanddamageinfo` geladen ist (dessen Live-Killcam besitzt die Todessequenz).
- Postfixes in Gameplay-Pfaden (OnDead, OnMakingShot) immer in try/catch.
- **RespawnApi-Interop** (seit 0.9.8): `src/Interop/RedeployRespawnInterop.cs` ist eine Kopie
  aus dem Redeploy-Repo (`interop/`), nur der Namespace ist geändert. Nie von Hand ändern,
  bei neuen Interop-Versionen neu kopieren. `LocalBodyTracker` ist `internal`; Felder dieses
  Typs müssen `internal` sein (CS0052). `BodyChanged` kann mitten in Cores Ersetzungs-Pipeline
  laufen: dort nur `StopPlayback` (fasst weder `PlayerCameraController` noch Bodies an), nie
  einen Respawn starten, den Raid stoppen oder einen Body zerstören. Im `OnDead`-Postfix gilt
  `ReferenceEquals(MainPlayer, __instance)` statt nur `IsYourPlayer`.
